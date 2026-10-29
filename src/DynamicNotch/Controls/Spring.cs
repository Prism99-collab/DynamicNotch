namespace DynamicNotch.Controls;

/// <summary>
/// A real damped spring (like iOS / SwiftUI). Unlike a fixed-duration animation it keeps its velocity
/// when the target changes mid-flight, so interrupted motion stays fluid instead of stopping and restarting.
/// </summary>
public sealed class Spring
{
    double _stiffness, _damping;

    public double Value;
    public double Velocity;
    public double Target;
    /// <summary>When false the value is driven by someone else (e.g. a finger drag) and the spring rests.</summary>
    public bool Active = true;

    public Spring(double value = 0) { Value = Target = value; Configure(SpringStyle.Pop); }

    /// <param name="response">Seconds for one (undamped) oscillation; lower = snappier.</param>
    /// <param name="dampingRatio">1 = no bounce, &lt;1 = a little overshoot.</param>
    public void Configure(double response, double dampingRatio)
    {
        double w = 2 * Math.PI / response;
        _stiffness = w * w;
        _damping = 2 * dampingRatio * w;
    }

    public void Configure(SpringStyle s)
    {
        switch (s)
        {
            case SpringStyle.Open: Configure(0.46, 0.80); break;
            case SpringStyle.Close: Configure(0.36, 1.0); break;
            case SpringStyle.Pop: Configure(0.40, 0.72); break;
            case SpringStyle.Peek: Configure(0.26, 0.74); break;
            case SpringStyle.Press: Configure(0.18, 0.9); break;
            case SpringStyle.Page: Configure(0.42, 0.9); break;
        }
    }

    public void Snap(double v) { Value = Target = v; Velocity = 0; }

    /// <summary>Advances by dt seconds; returns true once settled.</summary>
    public bool Step(double dt)
    {
        if (!Active) return true;
        if (IsSettled) { Value = Target; Velocity = 0; return true; }
        // fixed small sub-steps keep it stable on slow frames
        const double h = 1.0 / 480;
        for (double t = 0; t < dt; t += h)
        {
            double step = Math.Min(h, dt - t);
            double force = -_stiffness * (Value - Target) - _damping * Velocity;
            Velocity += force * step;
            Value += Velocity * step;
        }
        return false;
    }

    public bool IsSettled => Math.Abs(Value - Target) < 0.02 && Math.Abs(Velocity) < 0.6;
}

public enum SpringStyle { Open, Close, Pop, Peek, Press, Page }
