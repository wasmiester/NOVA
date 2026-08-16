using System;

namespace Nova;

// The "VU-meter" idle-liveliness technique used throughout the overlay
// skins (ARC's core/ring/spoke glow, WEB's equalizer bars, AURA's
// wifi-badge arcs): occasionally jump the target to a new random value,
// then ease Value toward it a little on every call to Update(). No real
// audio signal drives any of this - it's a cheap stand-in that reads as
// "alive" without one, shared here instead of copy-pasted per skin.
internal sealed class SmoothedRandomWalk
{
    private readonly Random _random;
    private readonly double _min;
    private readonly double _max;
    private readonly double _jumpChance;
    private readonly double _easing;
    private double _target;

    public double Value { get; private set; }

    public SmoothedRandomWalk(double initial, double min, double max, double jumpChance = 0.05, double easing = 0.15, Random? random = null)
    {
        _random = random ?? Random.Shared;
        _min = min;
        _max = max;
        _jumpChance = jumpChance;
        _easing = easing;
        Value = initial;
        _target = initial;
    }

    // Call once per tick/frame.
    public void Update()
    {
        if (_random.NextDouble() < _jumpChance)
        {
            _target = _min + _random.NextDouble() * (_max - _min);
        }

        Value += (_target - Value) * _easing;
    }

    // Eases toward `target` without ever taking a random jump - used to
    // bring the value to rest (e.g. ARC's stable idle glow) instead of the
    // usual jump-and-drift liveliness Update() gives.
    public void Settle(double target)
    {
        _target = target;
        Value += (target - Value) * _easing;
    }
}
