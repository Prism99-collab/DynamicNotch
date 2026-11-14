namespace DynamicNotch.Services;

/// <summary>
/// Tells a voice from the room. Works in decibels against a noise floor it keeps learning (it drops at once to the
/// quietest sound, and creeps up slowly, so a fan, a hum or a PC's fans sink into it after a few seconds while speech,
/// which always has pauses, never does). A voice opens the gate when it stands clearly above that floor, and keeps it
/// open through the short gaps between words. The level is scaled between the floor and how loud you've been lately,
/// so a quiet mic and a loud one both fill the bars without pinning them at the top.
/// </summary>
public sealed class VoiceGate
{
    double _floor = -60, _top = -30, _hold;
    bool _primed;

    /// <summary>Above the floor by this much (dB) to count as voice; below the second to let go of it.</summary>
    const double Open = 9, Close = 5, HoldSeconds = 0.22;

    /// <summary>How loud you are, 0–1, between the room's noise and your recent loudest.</summary>
    public double Level { get; private set; }

    /// <summary>Whether that's a voice (true) or just the room (false).</summary>
    public bool Voiced { get; private set; }

    /// <summary>The raw sound above the floor, voice or not, 0–1 (for faint noise bars).</summary>
    public double Noise { get; private set; }

    public void Reset() { _primed = false; _hold = 0; Level = Noise = 0; Voiced = false; }

    /// <param name="peak">The mic's peak, 0–1, for the last <paramref name="dt"/> seconds.</param>
    public void Feed(double peak, double dt)
    {
        dt = Math.Clamp(dt, 0.001, 0.5);
        double db = 20 * Math.Log10(Math.Max(peak, 1e-5)); // -100…0
        if (!_primed) { _floor = Math.Min(db, -45); _top = _floor + 30; _primed = true; }

        // the floor: down fast to anything quieter, up slowly (about 2 dB a second) while it's louder
        if (db < _floor) _floor += (db - _floor) * (1 - Math.Exp(-dt / 0.25));
        else if (!Voiced) _floor += Math.Min(db - _floor, 2.0 * dt);
        else _floor += Math.Min(db - _floor, 0.4 * dt); // speaking: barely moves
        _floor = Math.Clamp(_floor, -80, -22);

        double above = db - _floor;
        bool loudEnough = db > -58; // a whisper into a decent mic is around -45
        if (loudEnough && above > Open) { Voiced = true; _hold = HoldSeconds; }
        else if (Voiced && (above < Close || !loudEnough))
        {
            _hold -= dt;
            if (_hold <= 0) Voiced = false;
        }
        else if (Voiced) _hold = HoldSeconds;

        // your recent loudest: jumps up with you, eases down (3 dB a second), never closer than 22 dB to the floor
        if (Voiced && db > _top) _top = db;
        else _top -= 3 * dt;
        _top = Math.Max(_top, _floor + 22);

        double span = _top - _floor - 3;
        double x = Math.Clamp((db - _floor - 3) / span, 0, 1);
        Noise = Math.Clamp(above / 24, 0, 1);
        Level = Voiced ? Math.Pow(x, 0.75) : 0;
    }
}
