using Content.Shared.Administration;
using Content.Shared.CCVar.CVarAccess;
using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    /// <summary>
    /// Whether hitscan shots are checked against where the shooter saw other mobs, rather than where they are now.
    /// </summary>
    [CVarControl(AdminFlags.VarEdit)]
    public static readonly CVarDef<bool> CombatGunLagCompensation =
        CVarDef.Create("combat.gun_lag_compensation", true, CVar.SERVERONLY);

    /// <summary>
    /// Hard cap in seconds on how far back a shot can be rewound, no matter the shooter's ping.
    /// </summary>
    [CVarControl(AdminFlags.VarEdit)]
    public static readonly CVarDef<float> CombatGunLagCompensationMax =
        CVarDef.Create("combat.gun_lag_compensation_max", 0.5f, CVar.SERVERONLY);

    /// <summary>
    /// Seconds of rewind allowed on top of the shooter's ping, to cover the client's state buffer and interpolation.
    /// Claims of seeing an older state than this are clamped, so clients can't pick an arbitrarily old target position.
    /// </summary>
    [CVarControl(AdminFlags.VarEdit)]
    public static readonly CVarDef<float> CombatGunLagCompensationSlack =
        CVarDef.Create("combat.gun_lag_compensation_slack", 0.1f, CVar.SERVERONLY);
}
