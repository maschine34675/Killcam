using System;
using System.Collections.Generic;

namespace Killcam.Patches;

/// <summary>
/// Which PerfectCulling samplers are stale when another one starts, without searching the scene.
/// The samplers that started before (seen by the Start prefix) plus the sampler class's static
/// Instance cover every sampler that can make the new one throw. Pure: the liveness test is
/// passed in (Unity's overloaded null in the game), so offline checks can link this file.
/// Copy of Redeploy.Core/Compat/StaleSamplerRegistry.cs (tested there and in tools/HandoffChecks); only the namespace differs.
/// </summary>
internal sealed class StaleSamplerRegistry
{
    private readonly Func<object, bool> _isAlive;
    private readonly List<object> _started = new List<object>();

    public StaleSamplerRegistry(Func<object, bool> isAlive)
    {
        _isAlive = isAlive ?? throw new ArgumentNullException(nameof(isAlive));
    }

    /// <summary>Samplers still remembered (live or not yet found dead).</summary>
    public int Count => _started.Count;

    /// <summary>
    /// Returns the live samplers other than <paramref name="starting"/> that must go before it
    /// initializes: every one that started earlier, and <paramref name="current"/> (the class's
    /// static Instance) when that is another live sampler. Afterwards the registry holds the
    /// starting sampler and the returned ones, so a stale sampler that survives its destruction
    /// is offered again next time; dead ones are dropped.
    /// </summary>
    public List<object> TakeStale(object starting, object current)
    {
        var stale = new List<object>();
        foreach (var sampler in _started)
        {
            if (!ReferenceEquals(sampler, starting) && !stale.Contains(sampler) && _isAlive(sampler))
                stale.Add(sampler);
        }

        if (current != null && !ReferenceEquals(current, starting) && !stale.Contains(current) && _isAlive(current))
            stale.Add(current);

        _started.Clear();
        _started.AddRange(stale);
        if (starting != null)
            _started.Add(starting);
        return stale;
    }
}
