namespace KeyClick.Core;

public static class KeyClassifier
{
  public static InputGroup ClassifyKeyboard(int virtualKey) => virtualKey switch
  {
    >= 0x41 and <= 0x5A => InputGroup.Letters,
    >= 0x30 and <= 0x39 => InputGroup.Numbers,
    >= 0x60 and <= 0x6F => InputGroup.Numpad,
    >= 0x70 and <= 0x87 => InputGroup.FunctionAndMedia,
    0x20 => InputGroup.Space,
    0x0D => InputGroup.Enter,
    0x08 or 0x2E => InputGroup.Editing,
    0x14 or 0x90 or 0x91 => InputGroup.Locks,
    0x10 or 0x11 or 0x12 or 0x5B or 0x5C => InputGroup.Modifiers,
    0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D => InputGroup.Navigation,
    >= 0xBA and <= 0xE2 => InputGroup.Punctuation,
    _ => InputGroup.FunctionAndMedia
  };

  public static InputGroup ClassifyPointer(int buttonOrWheelCode) => buttonOrWheelCode switch
  {
    1 => InputGroup.PointerPrimary,
    2 => InputGroup.PointerSecondary,
    3 or 4 or 5 => InputGroup.PointerAuxiliary,
    _ => InputGroup.Wheel
  };
}
