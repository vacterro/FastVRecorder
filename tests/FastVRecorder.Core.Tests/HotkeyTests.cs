using FastVRecorder.Core.Hotkeys;
using Xunit;

namespace FastVRecorder.Core.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void DefaultToggle_IsAltMainRowZero()
    {
        Assert.Equal(HotkeyModifiers.Alt, HotkeyGesture.DefaultToggle.Modifiers);
        Assert.Equal(0x30, HotkeyGesture.DefaultToggle.VirtualKey);
        Assert.Equal("Alt + 0", HotkeyGesture.DefaultToggle.ToString());
        Assert.Equal("Alt+0", HotkeyGesture.DefaultToggle.Serialize());
    }

    [Theory]
    [InlineData("Alt+0")]
    [InlineData("Ctrl+Shift+F5")]
    [InlineData("Win+Alt+Space")]
    [InlineData("Ctrl+Alt+Numpad0")]
    [InlineData("Shift+=")]
    public void Gestures_RoundTripThroughStorageString(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        var serialized = gesture.Serialize();
        Assert.True(HotkeyGesture.TryParse(serialized, out var parsed));
        Assert.Equal(gesture, parsed);
    }

    [Fact]
    public void NumpadZero_IsDistinctFromMainRowZero()
    {
        var numpad = new HotkeyGesture(HotkeyModifiers.Alt, HotkeyGesture.VkNumpad0);
        Assert.NotEqual(HotkeyGesture.DefaultToggle, numpad);
        Assert.Equal("Num 0", HotkeyGesture.KeyName(HotkeyGesture.VkNumpad0));
        Assert.True(HotkeyGesture.TryParse("Alt+Num 0", out var parsed));
        Assert.Equal(numpad, parsed);
    }

    [Theory]
    [InlineData("Hyper+0")]
    [InlineData("Alt+NotAKey")]
    [InlineData("")]
    public void InvalidStringsAreRejectedOrEmpty(string text)
    {
        var parsed = HotkeyGesture.TryParse(text, out var gesture);
        if (text.Length == 0)
        {
            Assert.True(parsed);
            Assert.True(gesture.IsEmpty);
        }
        else
        {
            Assert.False(parsed);
        }
    }

    [Fact]
    public void Validator_RejectsReservedAndTypingBlockers()
    {
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.Win, 0x4C), HotkeyScope.Global));
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.Alt, 0x09), HotkeyScope.Global));
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x2E), HotkeyScope.Global));
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.None, 0x41), HotkeyScope.Global));
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.Shift, 0x41), HotkeyScope.Global));
        Assert.NotNull(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.None, 0x10), HotkeyScope.Global));

        Assert.Null(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.Alt, 0x30), HotkeyScope.Global));
        Assert.Null(HotkeyValidator.Validate(new HotkeyGesture(HotkeyModifiers.None, 0x70), HotkeyScope.Global));
        Assert.Null(HotkeyValidator.Validate(default, HotkeyScope.Global));
    }

    [Fact]
    public void FindDuplicate_DetectsSameGestureOnAnotherAction()
    {
        var bindings = new[]
        {
            new HotkeyBinding(HotkeyAction.ToggleRecording, HotkeyGesture.DefaultToggle, HotkeyScope.Global),
            new HotkeyBinding(HotkeyAction.StartRecording, default, HotkeyScope.Global),
        };

        Assert.Equal(HotkeyAction.ToggleRecording,
            HotkeyValidator.FindDuplicate(bindings, HotkeyAction.StartRecording, HotkeyGesture.DefaultToggle));
        Assert.Null(HotkeyValidator.FindDuplicate(bindings, HotkeyAction.ToggleRecording, HotkeyGesture.DefaultToggle));
        Assert.Null(HotkeyValidator.FindDuplicate(bindings, HotkeyAction.StartRecording, default));
    }

    [Fact]
    public void Defaults_ContainEveryActionExactlyOnce()
    {
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, HotkeyDefaults.All.Count);
        Assert.Equal(HotkeyDefaults.All.Count, HotkeyDefaults.All.Select(b => b.Action).Distinct().Count());
        Assert.Equal("Alt+0", HotkeyDefaults.For(HotkeyAction.ToggleRecording).Gesture.Serialize());
        Assert.True(HotkeyDefaults.For(HotkeyAction.StartRecording).Gesture.IsEmpty);
    }
}
