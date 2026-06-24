using System.Collections.Generic;

namespace Macrofy.App.ViewModels;

// A full-size (104-key) keyboard. Rows feed the UI; SetPressed lights keys live.
public sealed class KeyboardLayoutViewModel
{
    private const double Unit = 30;       // px per 1u key
    public double KeyHeight => 30;

    private readonly List<List<KeyCapViewModel>> _rows = new();
    private readonly Dictionary<int, List<KeyCapViewModel>> _byVk = new();

    public IReadOnlyList<IReadOnlyList<KeyCapViewModel>> Rows => _rows;

    public KeyboardLayoutViewModel() => Build();

    public void SetPressed(int vk, bool pressed)
    {
        if (_byVk.TryGetValue(vk, out var caps))
            foreach (var cap in caps)
                cap.IsPressed = pressed;
    }

    public void Reset()
    {
        foreach (var row in _rows)
            foreach (var cap in row)
                cap.IsPressed = false;
    }

    private List<KeyCapViewModel> _current = null!;

    private void Row() => _rows.Add(_current = new List<KeyCapViewModel>());

    private void K(string label, int vk, double u = 1)
    {
        var cap = new KeyCapViewModel(label, vk, u * Unit);
        _current.Add(cap);
        if (!_byVk.TryGetValue(vk, out var list))
            _byVk[vk] = list = new List<KeyCapViewModel>();
        list.Add(cap);
    }

    private void Sp(double u) => _current.Add(new KeyCapViewModel(string.Empty, null, u * Unit, isSpacer: true));

    private void Build()
    {
        // Function row
        Row();
        K("Esc", 0x1B); Sp(1);
        K("F1", 0x70); K("F2", 0x71); K("F3", 0x72); K("F4", 0x73); Sp(0.5);
        K("F5", 0x74); K("F6", 0x75); K("F7", 0x76); K("F8", 0x77); Sp(0.5);
        K("F9", 0x78); K("F10", 0x79); K("F11", 0x7A); K("F12", 0x7B); Sp(0.5);
        K("PrSc", 0x2C); K("ScLk", 0x91); K("Pause", 0x13); Sp(0.5);
        Sp(4);

        // Number row
        Row();
        K("~", 0xC0); K("1", 0x31); K("2", 0x32); K("3", 0x33); K("4", 0x34); K("5", 0x35);
        K("6", 0x36); K("7", 0x37); K("8", 0x38); K("9", 0x39); K("0", 0x30);
        K("-", 0xBD); K("=", 0xBB); K("Bksp", 0x08, 2); Sp(0.5);
        K("Ins", 0x2D); K("Home", 0x24); K("PgUp", 0x21); Sp(0.5);
        K("Num", 0x90); K("/", 0x6F); K("*", 0x6A); K("-", 0x6D);

        // Tab row
        Row();
        K("Tab", 0x09, 1.5);
        K("Q", 0x51); K("W", 0x57); K("E", 0x45); K("R", 0x52); K("T", 0x54); K("Y", 0x59);
        K("U", 0x55); K("I", 0x49); K("O", 0x4F); K("P", 0x50); K("[", 0xDB); K("]", 0xDD);
        K("\\", 0xDC, 1.5); Sp(0.5);
        K("Del", 0x2E); K("End", 0x23); K("PgDn", 0x22); Sp(0.5);
        K("7", 0x67); K("8", 0x68); K("9", 0x69); K("+", 0x6B);

        // Caps row
        Row();
        K("Caps", 0x14, 1.75);
        K("A", 0x41); K("S", 0x53); K("D", 0x44); K("F", 0x46); K("G", 0x47); K("H", 0x48);
        K("J", 0x4A); K("K", 0x4B); K("L", 0x4C); K(";", 0xBA); K("'", 0xDE);
        K("Enter", 0x0D, 2.25); Sp(0.5);
        Sp(3); Sp(0.5);
        K("4", 0x64); K("5", 0x65); K("6", 0x66); Sp(1);

        // Shift row
        Row();
        K("Shift", 0xA0, 2.25);
        K("Z", 0x5A); K("X", 0x58); K("C", 0x43); K("V", 0x56); K("B", 0x42); K("N", 0x4E);
        K("M", 0x4D); K(",", 0xBC); K(".", 0xBE); K("/", 0xBF);
        K("Shift", 0xA1, 2.75); Sp(0.5);
        Sp(1); K("↑", 0x26); Sp(1); Sp(0.5);
        K("1", 0x61); K("2", 0x62); K("3", 0x63); K("Ent", 0x0D);

        // Control row
        Row();
        K("Ctrl", 0xA2, 1.25); K("Win", 0x5B, 1.25); K("Alt", 0xA4, 1.25);
        K("Space", 0x20, 6.25);
        K("Alt", 0xA5, 1.25); K("Win", 0x5C, 1.25); K("Menu", 0x5D, 1.25); K("Ctrl", 0xA3, 1.25); Sp(0.5);
        K("←", 0x25); K("↓", 0x28); K("→", 0x27); Sp(0.5);
        K("0", 0x60, 2); K(".", 0x6E); Sp(1);
    }
}
