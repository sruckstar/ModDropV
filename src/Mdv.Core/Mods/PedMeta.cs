using System.Security;
using System.Text;

namespace Mdv.Core.Mods;

/// <summary>Which ped template a generated peds.meta entry is made from.</summary>
public enum PedGender { Male, Female }

/// <summary>
/// A ped the mod brings as models only (no peds.meta): its name, whether its components live in a folder
/// of its own (a streamed ped, <c>IsStreamedGfx</c>), whether it has props (<c>name_p.ydd</c>), and the
/// template its entry is written from — the player can change that before installing.
/// </summary>
public sealed class NewPed(string name, bool streamed, bool hasProps, PedGender gender)
{
    public string Name { get; } = name;
    public bool Streamed { get; } = streamed;
    public bool HasProps { get; } = hasProps;
    public PedGender Gender { get; set; } = gender;
}

/// <summary>
/// peds.meta for peds that come without one: an ambient ped's entry (Rockstar's own, from the club
/// customers of mpbattle — a DLC both editions have) with the name, the streamed flag and the props filled in.
/// Male and female differ in the ped type, walk, gestures, face, voice and capsule.
/// </summary>
public static class PedMeta
{
    /// <summary>A guess from the name: <c>a_f_y_…</c>, <c>…female…</c>, <c>…_f_…</c> are female, the rest male.</summary>
    public static PedGender Guess(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("female") || n.StartsWith("f_", StringComparison.Ordinal) || n.Contains("_f_") || n.EndsWith("_f", StringComparison.Ordinal)
            ? PedGender.Female : PedGender.Male;
    }

    public static string Build(IEnumerable<NewPed> peds)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<CPedModelInfo__InitDataList>\n  <residentTxd>comp_peds_generic</residentTxd>\n")
          .Append("  <residentAnims />\n  <InitDatas>\n");
        foreach (var p in peds) sb.Append(Item(p));
        sb.Append("  </InitDatas>\n  <txdRelationships />\n  <multiTxdRelationships />\n</CPedModelInfo__InitDataList>\n");
        return sb.ToString();
    }

    private static string Item(NewPed p)
    {
        bool f = p.Gender == PedGender.Female;
        string name = SecurityElement.Escape(p.Name);
        string B(bool v) => v ? "true" : "false";
        return $"""
                    <Item>
                      <Name>{name}</Name>
                      <PropsName>{(p.HasProps ? name + "_p" : "null")}</PropsName>
                      <ClipDictionaryName>{(f ? "move_f@generic" : "move_m@generic")}</ClipDictionaryName>
                      <BlendShapeFileName>null</BlendShapeFileName>
                      <ExpressionSetName>{(f ? "expr_set_ambient_female" : "expr_set_ambient_male")}</ExpressionSetName>
                      <ExpressionDictionaryName>null</ExpressionDictionaryName>
                      <ExpressionName>null</ExpressionName>
                      <Pedtype>{(f ? "CIVFEMALE" : "CIVMALE")}</Pedtype>
                      <MovementClipSet>{(f ? "move_f@generic" : "move_m@generic")}</MovementClipSet>
                      <StrafeClipSet>move_ped_strafing</StrafeClipSet>
                      <MovementToStrafeClipSet>move_ped_to_strafe</MovementToStrafeClipSet>
                      <InjuredStrafeClipSet>move_strafe_injured</InjuredStrafeClipSet>
                      <FullBodyDamageClipSet>dam_ko</FullBodyDamageClipSet>
                      <AdditiveDamageClipSet>dam_ad</AdditiveDamageClipSet>
                      <DefaultGestureClipSet>{(f ? "ANIM_GROUP_GESTURE_F_GENERIC" : "ANIM_GROUP_GESTURE_M_GENERIC")}</DefaultGestureClipSet>
                      <FacialClipsetGroupName>{(f ? "facial_clipset_group_gen_female" : "facial_clipset_group_gen_male")}</FacialClipsetGroupName>
                      <DefaultVisemeClipSet>{(f ? "ANIM_GROUP_VISEMES_F_LO" : "ANIM_GROUP_VISEMES_M_LO")}</DefaultVisemeClipSet>
                      <SidestepClipSet>CLIP_SET_ID_INVALID</SidestepClipSet>
                      <PoseMatcherName>Male</PoseMatcherName>
                      <PoseMatcherProneName>Male_prone</PoseMatcherProneName>
                      <GetupSetHash>NMBS_SLOW_GETUPS</GetupSetHash>
                      <CreatureMetadataName>null</CreatureMetadataName>
                      <DecisionMakerName>DEFAULT</DecisionMakerName>
                      <MotionTaskDataSetName>STANDARD_PED</MotionTaskDataSetName>
                      <DefaultTaskDataSetName>STANDARD_PED</DefaultTaskDataSetName>
                      <PedCapsuleName>{(f ? "STANDARD_FEMALE" : "STANDARD_MALE")}</PedCapsuleName>
                      <PedLayoutName />
                      <PedComponentSetName />
                      <PedComponentClothName />
                      <PedIKSettingsName />
                      <TaskDataName />
                      <IsStreamedGfx value="{B(p.Streamed)}" />
                      <AmbulanceShouldRespondTo value="true" />
                      <CanRideBikeWithNoHelmet value="false" />
                      <CanSpawnInCar value="true" />
                      <IsHeadBlendPed value="false" />
                      <bOnlyBulkyItemVariations value="false" />
                      <RelationshipGroup>{(f ? "CIVFEMALE" : "CIVMALE")}</RelationshipGroup>
                      <NavCapabilitiesName>STANDARD_PED</NavCapabilitiesName>
                      <PerceptionInfo>DEFAULT_PERCEPTION</PerceptionInfo>
                      <DefaultBrawlingStyle>BS_AI</DefaultBrawlingStyle>
                      <DefaultUnarmedWeapon>WEAPON_UNARMED</DefaultUnarmedWeapon>
                      <Personality>{(f ? "YOUNGRICHWOMAN" : "YOUNGAVERAGEWEAKMAN")}</Personality>
                      <CombatInfo>DEFAULT</CombatInfo>
                      <VfxInfoName>VFXPEDINFO_HUMAN_GENERIC</VfxInfoName>
                      <AmbientClipsForFlee>FLEE</AmbientClipsForFlee>
                      <Radio1>RADIO_GENRE_PUNK</Radio1>
                      <Radio2>RADIO_GENRE_JAZZ</Radio2>
                      <FUpOffset value="0.000000" />
                      <RUpOffset value="0.000000" />
                      <FFrontOffset value="0.000000" />
                      <RFrontOffset value="0.147000" />
                      <MinActivationImpulse value="20.000000" />
                      <Stubble value="0.000000" />
                      <HDDist value="3.000000" />
                      <TargetingThreatModifier value="1.000000" />
                      <KilledPerceptionRangeModifer value="-1.000000" />
                      <Sexiness>{(f ? "SF_HOT_PERSON" : "SF_JEER_AT_HOT_PED")}</Sexiness>
                      <Age value="0" />
                      <MaxPassengersInCar value="0" />
                      <ExternallyDrivenDOFs />
                      <PedVoiceGroup>{(f ? "FEMALE_CLUB_R2PVG" : "MALE_CLUB_R2PVG")}</PedVoiceGroup>
                      <AnimalAudioObject />
                      <AbilityType>SAT_NONE</AbilityType>
                      <ThermalBehaviour>TB_WARM</ThermalBehaviour>
                      <SuperlodType>SLOD_HUMAN</SuperlodType>
                      <ScenarioPopStreamingSlot>SCENARIO_POP_STREAMING_NORMAL</ScenarioPopStreamingSlot>
                      <DefaultSpawningPreference>DSP_NORMAL</DefaultSpawningPreference>
                      <DefaultRemoveRangeMultiplier value="1.000000" />
                      <AllowCloseSpawning value="false" />
                    </Item>

                """;
    }
}
