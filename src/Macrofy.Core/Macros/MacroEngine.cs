using Macrofy.Core.Input;

namespace Macrofy.Core.Macros;

// Turns captured key-presses into macro actions, with layer support. A profile holds one
// or more layers; LayerHold/LayerToggle bindings change which layer is "active", and a key
// undefined on the active layer falls through to Base (so layers can be sparse). Lookups
// are pre-built into per-layer maps because OnCapturedKey runs on the time-critical decider
// thread and must return fast; real actions run on a thread-pool thread.
public sealed class MacroEngine
{
    private readonly object _gate = new();

    private string[] _layerNames = Array.Empty<string>();
    private Dictionary<int, MacroBinding>[] _layerMaps = Array.Empty<Dictionary<int, MacroBinding>>();

    private int _activeLayer;       // index into _layerMaps
    private int _holdSourceLayer;   // layer to return to when a held layer key releases
    private int _holdVk;            // vk currently holding a layer (0 = none)

    // Fired (on the decider thread) when the active layer changes, arg = new layer index.
    public event EventHandler<int>? ActiveLayerChanged;

    public void SetProfile(MacroProfile profile)
    {
        lock (_gate)
        {
            profile.Normalize();
            _layerNames = profile.Layers.Select(l => l.Name).ToArray();
            _layerMaps = profile.Layers.Select(BuildMap).ToArray();

            // Keep the current layer if it's still in range, so live edits don't yank it.
            if (_activeLayer >= _layerMaps.Length)
                _activeLayer = 0;
            _holdVk = 0;
        }
        ActiveLayerChanged?.Invoke(this, _activeLayer);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _layerNames = Array.Empty<string>();
            _layerMaps = Array.Empty<Dictionary<int, MacroBinding>>();
            _activeLayer = 0;
            _holdVk = 0;
        }
        ActiveLayerChanged?.Invoke(this, 0);
    }

    private static Dictionary<int, MacroBinding> BuildMap(MacroLayer layer)
    {
        var map = new Dictionary<int, MacroBinding>();
        foreach (var b in layer.Bindings)
            if (!b.IsEmpty)
                map[b.VirtualKey] = b;
        return map;
    }

    // Called from the capture backend (decider thread) - must return fast.
    public void OnCapturedKey(DeviceKeyEvent e)
    {
        MacroBinding? toRun = null;
        bool layerChanged = false;
        int newLayer = 0;

        lock (_gate)
        {
            if (_layerMaps.Length == 0)
                return;

            if (!e.IsKeyDown)
            {
                // Releasing the key that engaged a momentary layer returns us to where we were.
                if (_holdVk != 0 && e.VirtualKey == _holdVk)
                {
                    _activeLayer = _holdSourceLayer;
                    _holdVk = 0;
                    layerChanged = true;
                    newLayer = _activeLayer;
                }
            }
            else
            {
                var binding = Resolve(e.VirtualKey);
                switch (binding?.Action.Kind)
                {
                    case MacroActionKind.LayerHold:
                    {
                        int target = IndexOfLayer(binding.Action.Target);
                        if (target >= 0 && target != _activeLayer)
                        {
                            _holdSourceLayer = _activeLayer;
                            _holdVk = e.VirtualKey;
                            _activeLayer = target;
                            layerChanged = true;
                            newLayer = target;
                        }
                        break;
                    }
                    case MacroActionKind.LayerToggle:
                    {
                        int target = IndexOfLayer(binding.Action.Target);
                        if (target >= 0)
                        {
                            _activeLayer = _activeLayer == target ? 0 : target;
                            _holdVk = 0;
                            layerChanged = true;
                            newLayer = _activeLayer;
                        }
                        break;
                    }
                    default:
                        toRun = binding; // a normal single action or a multi-step sequence
                        break;
                }
            }
        }

        if (layerChanged)
            ActiveLayerChanged?.Invoke(this, newLayer);
        if (toRun is not null)
            Task.Run(() => MacroExecutor.Run(toRun));
    }

    // Active layer first; if the key isn't defined there, fall through to Base (transparent).
    private MacroBinding? Resolve(int vk)
    {
        if (_layerMaps[_activeLayer].TryGetValue(vk, out var b))
            return b;
        if (_activeLayer != 0 && _layerMaps[0].TryGetValue(vk, out var baseB))
            return baseB;
        return null;
    }

    private int IndexOfLayer(string name)
    {
        for (int i = 0; i < _layerNames.Length; i++)
            if (string.Equals(_layerNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }
}
