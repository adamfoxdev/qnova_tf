using System.Numerics;

namespace Qnova.Core;

public sealed partial class GameWorld
{
    public const float MedicRange = 320f;
    const float MedicSelfHeal = 2f;

    /// <summary>Spawn as <see cref="Player.NextClass"/>: class health and speed, and its loadout with fresh ammo.</summary>
    void ApplyClass(Player p)
    {
        var d = ClassDef.Get(p.NextClass == PlayerClass.None ? PlayerClass.Soldier : p.NextClass);
        p.Class = d.Id; p.MaxHealth = d.Health; p.Health = d.Health; p.HealBuffer = 0;
        p.Move.SpeedScale = d.Speed;
        p.Owned = new HashSet<WeaponId>(d.Weapons);
        p.Current = d.Primary;
        p.Shells = d.Shells; p.Nails = d.Nails; p.Rockets = d.Rockets; p.Cells = d.Cells; p.Slugs = d.Slugs;
    }

    /// <summary>Choose the class for your next life. Takes effect when you respawn (use <c>kill</c> to do it now).</summary>
    public void ChooseClass(PlayerClass c)
    {
        Player.NextClass = c;
        Console.Print($"You will respawn as a {ClassDef.Get(c).Name}");
    }

    /// <summary>Medics heal wounded teammates in range (line of sight required) and slowly patch themselves up.</summary>
    void UpdateMedics()
    {
        if (!IsTf) return;
        foreach (var m in Combatants)
        {
            if (!m.Alive || m.Class != PlayerClass.Medic) continue;
            float aura = ClassDef.Get(PlayerClass.Medic).HealAura;
            foreach (var o in Combatants)
            {
                if (!o.Alive || o.Health >= o.MaxHealth) continue;
                float rate;
                if (o == m) rate = MedicSelfHeal;
                else if (Friendly(m, o) && Vector3.Distance(m.Move.Position, o.Move.Position) <= MedicRange && Visible(m.Eye, o.Move.Position)) rate = aura;
                else continue;
                o.HealBuffer += rate * Dt;
                if (o.HealBuffer >= 1f) { int n = (int)o.HealBuffer; o.HealBuffer -= n; o.Health = Math.Min(o.MaxHealth, o.Health + n); }
            }
        }
    }
}
