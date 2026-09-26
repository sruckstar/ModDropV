using System.Numerics;
using System.Runtime.CompilerServices;
using Mdv.Core.Preview;

namespace Mdv.App.Rendering;

/// <summary>An orbit camera around <see cref="Target"/>; Z is up, yaw 0 looks from −Y (the side).</summary>
public readonly record struct OrbitCamera(float Yaw, float Pitch, float Distance, Vector3 Target, float FovY)
{
    public Vector3 Eye => Target + Distance * new Vector3(
        MathF.Sin(Yaw) * MathF.Cos(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch));

    /// <summary>Right / up / forward of the view, in world space.</summary>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) Basis()
    {
        var f = Vector3.Normalize(Target - Eye);
        var r = Vector3.Cross(f, Vector3.UnitZ);
        r = r.LengthSquared() < 1e-8f ? Vector3.UnitX : Vector3.Normalize(r);
        return (r, Vector3.Cross(r, f), f);
    }

    /// <summary>
    /// The distance at which the box [min, max] fills the view (leaving <paramref name="margin"/>
    /// of it free) seen from this camera's angles, looking at the box centre.
    /// </summary>
    public float FitDistance(Vector3 min, Vector3 max, float aspect, float margin)
    {
        var center = (min + max) * 0.5f;
        var (r, u, f) = (this with { Target = center, Distance = 1 }).Basis();
        float tanY = MathF.Tan(FovY * 0.5f) * (1 - margin), tanX = tanY * aspect;
        float d = 0;
        for (int i = 0; i < 8; i++)
        {
            var c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) - center;
            float z = Vector3.Dot(c, f);
            d = MathF.Max(d, MathF.Max(MathF.Abs(Vector3.Dot(c, r)) / tanX, MathF.Abs(Vector3.Dot(c, u)) / tanY) - z);
        }
        return MathF.Max(d, (max - min).Length() * 0.5f * 1.05f);
    }
}

/// <summary>
/// A small software rasterizer for the weapon preview: z-buffer, perspective-correct
/// texturing with mip-mapped bilinear sampling, per-pixel studio lighting, alpha cut-outs
/// and blended decals. Rows are split into bands rendered in parallel. Output is
/// premultiplied BGRA with a transparent background. One instance renders one frame at a time.
/// </summary>
public sealed class SoftRenderer
{
    private const float AlphaCut = 85;            // texels at or below ~1/3 alpha are cut out (as the game)

    /// <summary>A projected, clipped triangle: edge functions and attribute planes in screen space.</summary>
    private struct Tri
    {
        public float MinX, MaxX, MinY, MaxY;
        public float E0A, E0B, E0C, E1A, E1B, E1C, E2A, E2B, E2C;
        public Plane Iz, Uz, Vz, Nx, Ny, Nz;       // 1/z and attribute/z, linear in screen space
        public int Mesh;
        public int Lod;
        public float Flip;                         // −1 when the camera sees the back of the face
    }

    private struct Plane
    {
        public float A, B, C;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly float At(float x, float y) => A * x + B * y + C;
    }

    private struct ClipVert
    {
        public Vector3 P;    // view space
        public Vector3 N;    // view space
        public float U, V;
    }

    private float[] _depth = [];
    private Tri[] _tris = new Tri[4096];
    private int _triCount;
    private float[] _vp = [];                      // per-vertex view position scratch
    private float[] _vn = [];                      // per-vertex view normal scratch
    private PreviewTexture?[] _tex = [];           // per-mesh texture in the tint drawn

    public int LastTriangles => _triCount;

    /// <summary>
    /// Render <paramref name="meshes"/> into <paramref name="target"/> (w×h premultiplied BGRA),
    /// palette-coloured surfaces in weapon tint <paramref name="tint"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Render(IReadOnlyList<PreviewMesh> meshes, OrbitCamera cam, bool textured, uint[] target, int w, int h, int tint = 0)
    {
        if (w <= 0 || h <= 0) return;
        int n = w * h;
        if (_depth.Length < n) _depth = new float[n];
        Array.Clear(_depth, 0, n);                 // 1/z: 0 = infinitely far
        Array.Clear(target, 0, n);

        var (right, up, fwd) = cam.Basis();
        var eye = cam.Eye;
        float focal = h * 0.5f / MathF.Tan(cam.FovY * 0.5f);
        float near = Math.Max(cam.Distance * 0.01f, 0.0005f);
        _triCount = 0;
        if (_tex.Length < meshes.Count) _tex = new PreviewTexture?[meshes.Count];
        for (int m = 0; m < meshes.Count; m++)
            _tex[m] = textured ? meshes[m].TextureFor(tint) : null;
        for (int m = 0; m < meshes.Count; m++)
            Setup(meshes[m], m, eye, right, up, fwd, focal, near, w, h, _tex[m]);

        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int bandH = Math.Max(8, (h + bands - 1) / bands);
        bands = (h + bandH - 1) / bandH;
        var tris = _tris;
        int count = _triCount;
        var depth = _depth;
        var texs = _tex;
        Parallel.For(0, bands, b =>
        {
            int y0 = b * bandH, y1 = Math.Min(h, y0 + bandH);
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < count; i++)
                {
                    ref var t = ref tris[i];
                    if (t.MaxY < y0 || t.MinY >= y1) continue;
                    var mesh = meshes[t.Mesh];
                    bool decal = mesh.Kind == SurfaceKind.Decal && textured;
                    if (decal != (pass == 1)) continue;
                    Raster(ref t, texs[t.Mesh], decal, target, depth, w, y0, y1);
                }
        });
    }

    // ------------------------------------------------------------------ setup

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Setup(PreviewMesh mesh, int meshIndex, Vector3 eye, Vector3 right, Vector3 up, Vector3 fwd,
                       float focal, float near, int w, int h, PreviewTexture? tex)
    {
        int vc = mesh.VertexCount;
        if (_vp.Length < vc * 3)
        {
            _vp = new float[vc * 3];
            _vn = new float[vc * 3];
        }
        var pos = mesh.Positions;
        var nrm = mesh.Normals;
        for (int v = 0; v < vc; v++)
        {
            int o = v * 3;
            var d = new Vector3(pos[o] - eye.X, pos[o + 1] - eye.Y, pos[o + 2] - eye.Z);
            _vp[o] = Vector3.Dot(d, right);
            _vp[o + 1] = Vector3.Dot(d, up);
            _vp[o + 2] = Vector3.Dot(d, fwd);
            var nv = new Vector3(nrm[o], nrm[o + 1], nrm[o + 2]);
            _vn[o] = Vector3.Dot(nv, right);
            _vn[o + 1] = Vector3.Dot(nv, up);
            _vn[o + 2] = Vector3.Dot(nv, fwd);
        }

        var idx = mesh.Indices;
        var uv = mesh.Uvs;
        Span<ClipVert> inp = stackalloc ClipVert[3];
        Span<ClipVert> poly = stackalloc ClipVert[4];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int a = idx[i], b = idx[i + 1], c = idx[i + 2];
            float za = _vp[a * 3 + 2], zb = _vp[b * 3 + 2], zc = _vp[c * 3 + 2];
            if (za < near && zb < near && zc < near) continue;
            inp[0] = Vert(a, uv);
            inp[1] = Vert(b, uv);
            inp[2] = Vert(c, uv);

            // which side of the face the camera sees: its normals point away → the back
            var avgN = inp[0].N + inp[1].N + inp[2].N;
            var center = inp[0].P + inp[1].P + inp[2].P;
            float flip = Vector3.Dot(avgN, center) > 0 ? -1f : 1f;

            int pc;
            if (za >= near && zb >= near && zc >= near)
            {
                inp.CopyTo(poly);
                pc = 3;
            }
            else pc = ClipNear(inp, poly, near);
            for (int k = 1; k + 1 < pc; k++)
                Emit(poly[0], poly[k], poly[k + 1], meshIndex, tex, flip, focal, w, h);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ClipVert Vert(int v, float[] uv) => new()
    {
        P = new Vector3(_vp[v * 3], _vp[v * 3 + 1], _vp[v * 3 + 2]),
        N = new Vector3(_vn[v * 3], _vn[v * 3 + 1], _vn[v * 3 + 2]),
        U = uv[v * 2],
        V = uv[v * 2 + 1],
    };

    /// <summary>Sutherland–Hodgman against the near plane: a triangle becomes 0, 3 or 4 vertices.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int ClipNear(ReadOnlySpan<ClipVert> tri, Span<ClipVert> output, float near)
    {
        int count = 0;
        for (int i = 0; i < 3; i++)
        {
            var cur = tri[i];
            var nxt = tri[(i + 1) % 3];
            bool curIn = cur.P.Z >= near, nxtIn = nxt.P.Z >= near;
            if (curIn) output[count++] = cur;
            if (curIn != nxtIn)
            {
                float t = (near - cur.P.Z) / (nxt.P.Z - cur.P.Z);
                output[count++] = new ClipVert
                {
                    P = Vector3.Lerp(cur.P, nxt.P, t),
                    N = Vector3.Lerp(cur.N, nxt.N, t),
                    U = cur.U + (nxt.U - cur.U) * t,
                    V = cur.V + (nxt.V - cur.V) * t,
                };
            }
        }
        return count;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Emit(in ClipVert a, in ClipVert b, in ClipVert c, int mesh, PreviewTexture? tex, float flip,
                      float focal, int w, int h)
    {
        float cx = w * 0.5f, cy = h * 0.5f;
        float iza = 1 / a.P.Z, izb = 1 / b.P.Z, izc = 1 / c.P.Z;
        float x0 = cx + a.P.X * iza * focal, y0 = cy - a.P.Y * iza * focal;
        float x1 = cx + b.P.X * izb * focal, y1 = cy - b.P.Y * izb * focal;
        float x2 = cx + c.P.X * izc * focal, y2 = cy - c.P.Y * izc * focal;

        float minX = MathF.Min(x0, MathF.Min(x1, x2)), maxX = MathF.Max(x0, MathF.Max(x1, x2));
        float minY = MathF.Min(y0, MathF.Min(y1, y2)), maxY = MathF.Max(y0, MathF.Max(y1, y2));
        if (maxX < 0 || maxY < 0 || minX >= w || minY >= h) return;
        float area2 = (x1 - x0) * (y2 - y0) - (x2 - x0) * (y1 - y0);
        if (MathF.Abs(area2) < 1e-6f || !float.IsFinite(area2)) return;

        if (_triCount == _tris.Length) Array.Resize(ref _tris, _tris.Length * 2);
        ref var t = ref _tris[_triCount++];
        t.MinX = minX;
        t.MaxX = maxX;
        t.MinY = minY;
        t.MaxY = maxY;
        t.Mesh = mesh;
        t.Flip = flip;

        // edges oriented so the inside is positive whatever the winding
        float s = area2 > 0 ? 1 : -1;
        t.E0A = (y1 - y2) * s; t.E0B = (x2 - x1) * s; t.E0C = (x1 * y2 - x2 * y1) * s;
        t.E1A = (y2 - y0) * s; t.E1B = (x0 - x2) * s; t.E1C = (x2 * y0 - x0 * y2) * s;
        t.E2A = (y0 - y1) * s; t.E2B = (x1 - x0) * s; t.E2C = (x0 * y1 - x1 * y0) * s;

        t.Iz = PlaneOf(x0, y0, iza, x1, y1, izb, x2, y2, izc, area2);
        t.Uz = PlaneOf(x0, y0, a.U * iza, x1, y1, b.U * izb, x2, y2, c.U * izc, area2);
        t.Vz = PlaneOf(x0, y0, a.V * iza, x1, y1, b.V * izb, x2, y2, c.V * izc, area2);
        t.Nx = PlaneOf(x0, y0, a.N.X * iza, x1, y1, b.N.X * izb, x2, y2, c.N.X * izc, area2);
        t.Ny = PlaneOf(x0, y0, a.N.Y * iza, x1, y1, b.N.Y * izb, x2, y2, c.N.Y * izc, area2);
        t.Nz = PlaneOf(x0, y0, a.N.Z * iza, x1, y1, b.N.Z * izb, x2, y2, c.N.Z * izc, area2);

        // one mip level per triangle: texels per pixel over the whole face
        t.Lod = 0;
        if (tex is not null)
        {
            float uvArea = MathF.Abs((b.U - a.U) * (c.V - a.V) - (c.U - a.U) * (b.V - a.V)) * tex.Width * tex.Height;
            float ratio = uvArea / MathF.Abs(area2);
            if (ratio > 1 && float.IsFinite(ratio))
                t.Lod = Math.Min(tex.Levels.Length - 1, (int)(0.5f * MathF.Log2(ratio)));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Plane PlaneOf(float x0, float y0, float f0, float x1, float y1, float f1, float x2, float y2, float f2, float area2)
    {
        float a = ((f1 - f0) * (y2 - y0) - (f2 - f0) * (y1 - y0)) / area2;
        float b = ((f2 - f0) * (x1 - x0) - (f1 - f0) * (x2 - x0)) / area2;
        return new Plane { A = a, B = b, C = f0 - a * x0 - b * y0 };
    }

    // ------------------------------------------------------------------ raster

    // studio lights, fixed to the view (x right, y up, the camera looks along +z)
    private static readonly Vector3 KeyL = Vector3.Normalize(new(-0.55f, 0.65f, -0.55f));
    private static readonly Vector3 FillL = Vector3.Normalize(new(0.75f, 0.05f, -0.45f));
    private static readonly Vector3 RimL = Vector3.Normalize(new(0.25f, 0.55f, 0.8f));
    private static readonly Vector3 KeyH = Vector3.Normalize(KeyL + new Vector3(0, 0, -1));
    private static readonly Vector3 FillH = Vector3.Normalize(FillL + new Vector3(0, 0, -1));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void Raster(ref Tri t, PreviewTexture? tex, bool decal, uint[] color, float[] depth, int w, int y0, int y1)
    {
        int ys = Math.Max(y0, (int)MathF.Ceiling(t.MinY - 0.5f));
        int ye = Math.Min(y1 - 1, (int)MathF.Floor(t.MaxY - 0.5f));
        int bx0 = Math.Max(0, (int)MathF.Ceiling(t.MinX - 0.5f));
        int bx1 = Math.Min(w - 1, (int)MathF.Floor(t.MaxX - 0.5f));
        if (ys > ye || bx0 > bx1) return;

        uint[]? texels = null;
        int tw = 0, th = 0;
        bool cut = false;
        if (tex is not null)
        {
            texels = tex.Levels[t.Lod];
            tw = tex.LevelWidths[t.Lod];
            th = tex.LevelHeights[t.Lod];
            cut = tex.HasAlpha && !decal;
        }

        for (int y = ys; y <= ye; y++)
        {
            float py = y + 0.5f;
            // the span of this row inside all three edges
            float xl = bx0 + 0.5f, xr = bx1 + 0.5f;
            if (!Span(t.E0A, t.E0B * py + t.E0C, ref xl, ref xr)) continue;
            if (!Span(t.E1A, t.E1B * py + t.E1C, ref xl, ref xr)) continue;
            if (!Span(t.E2A, t.E2B * py + t.E2C, ref xl, ref xr)) continue;
            int xs = Math.Max(bx0, (int)MathF.Ceiling(xl - 0.5f));
            int xe = Math.Min(bx1, (int)MathF.Floor(xr - 0.5f));
            if (xs > xe) continue;

            float px0 = xs + 0.5f;
            float iz = t.Iz.At(px0, py), uz = t.Uz.At(px0, py), vz = t.Vz.At(px0, py);
            float nxz = t.Nx.At(px0, py), nyz = t.Ny.At(px0, py), nzz = t.Nz.At(px0, py);
            int row = y * w;
            for (int x = xs; x <= xe; x++, iz += t.Iz.A, uz += t.Uz.A, vz += t.Vz.A, nxz += t.Nx.A, nyz += t.Ny.A, nzz += t.Nz.A)
            {
                int i = row + x;
                if (decal ? iz < depth[i] * 0.9995f : iz <= depth[i]) continue;
                float z = 1 / iz;

                float r = 0.66f, g = 0.66f, b = 0.68f, a = 1;
                if (texels is not null)
                {
                    uint c = Sample(texels, tw, th, uz * z, vz * z);
                    float ca = c >> 24;
                    if (cut && ca <= AlphaCut) continue;
                    if (decal && ca < 2) continue;
                    r = ((c >> 16) & 0xFF) * (1 / 255f);
                    g = ((c >> 8) & 0xFF) * (1 / 255f);
                    b = (c & 0xFF) * (1 / 255f);
                    if (decal) a = ca * (1 / 255f);
                }

                var n = new Vector3(nxz * z, nyz * z, nzz * z);
                float len2 = n.LengthSquared();
                n = len2 > 1e-12f ? n * (t.Flip / MathF.Sqrt(len2)) : new Vector3(0, 0, -1);
                float key = MathF.Max(0, Vector3.Dot(n, KeyL));
                float fill = MathF.Max(0, Vector3.Dot(n, FillL));
                float rim = MathF.Max(0, Vector3.Dot(n, RimL));
                float light = 0.36f + 0.12f * (n.Y * 0.5f + 0.5f) + 1.05f * key + 0.40f * fill + 0.45f * rim;
                float spec = 0.42f * Pow32(MathF.Max(0, Vector3.Dot(n, KeyH))) + 0.15f * Pow32(MathF.Max(0, Vector3.Dot(n, FillH)));

                r = MathF.Min(1, r * light + spec);
                g = MathF.Min(1, g * light + spec);
                b = MathF.Min(1, b * light + spec);

                if (!decal)
                {
                    depth[i] = iz;
                    color[i] = 0xFF000000u | ((uint)(r * 255 + 0.5f) << 16) | ((uint)(g * 255 + 0.5f) << 8) | (uint)(b * 255 + 0.5f);
                }
                else
                {
                    uint d = color[i];
                    float ia = 1 - a;
                    uint oa = (uint)MathF.Min(255, a * 255 + (d >> 24) * ia + 0.5f);
                    uint or = (uint)MathF.Min(255, r * a * 255 + ((d >> 16) & 0xFF) * ia + 0.5f);
                    uint og = (uint)MathF.Min(255, g * a * 255 + ((d >> 8) & 0xFF) * ia + 0.5f);
                    uint ob = (uint)MathF.Min(255, b * a * 255 + (d & 0xFF) * ia + 0.5f);
                    color[i] = (oa << 24) | (or << 16) | (og << 8) | ob;
                }
            }
        }
    }

    /// <summary>Narrow [xl, xr] to where A·x + rowTerm ≥ 0; false when nothing is left.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Span(float a, float rowTerm, ref float xl, ref float xr)
    {
        // pixel centres sit at x + 0.5, so solve for the centre coordinate
        if (a > 1e-12f) xl = MathF.Max(xl, -rowTerm / a);
        else if (a < -1e-12f) xr = MathF.Min(xr, -rowTerm / a);
        else if (rowTerm < 0) return false;
        return xl <= xr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Pow32(float x)
    {
        x *= x; x *= x; x *= x; x *= x; x *= x;
        return x;
    }

    /// <summary>Bilinear, wrapping sample of one mip level.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Sample(uint[] px, int w, int h, float u, float v)
    {
        float fx = u * w - 0.5f, fy = v * h - 0.5f;
        if (!float.IsFinite(fx) || !float.IsFinite(fy) || MathF.Abs(fx) > 1e7f || MathF.Abs(fy) > 1e7f) return px[0];
        float flx = MathF.Floor(fx), fly = MathF.Floor(fy);
        int x0 = (int)flx % w, y0 = (int)fly % h;
        if (x0 < 0) x0 += w;
        if (y0 < 0) y0 += h;
        int x1 = x0 + 1 == w ? 0 : x0 + 1, y1 = y0 + 1 == h ? 0 : y0 + 1;
        int wx = (int)((fx - flx) * 256), wy = (int)((fy - fly) * 256);
        uint c00 = px[y0 * w + x0], c10 = px[y0 * w + x1], c01 = px[y1 * w + x0], c11 = px[y1 * w + x1];
        uint result = 0;
        for (int sh = 0; sh < 32; sh += 8)
        {
            int top = (int)((c00 >> sh) & 0xFF) * (256 - wx) + (int)((c10 >> sh) & 0xFF) * wx;
            int bot = (int)((c01 >> sh) & 0xFF) * (256 - wx) + (int)((c11 >> sh) & 0xFF) * wx;
            result |= (uint)(((top * (256 - wy) + bot * wy) >> 16) & 0xFF) << sh;
        }
        return result;
    }
}
