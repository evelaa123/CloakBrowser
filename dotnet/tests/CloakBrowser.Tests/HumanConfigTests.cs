using CloakBrowser.Human;
using Xunit;

namespace CloakBrowser.Tests;

public class HumanConfigTests
{
    [Fact]
    public void DefaultPreset_HasExpectedDefaults()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default);
        Assert.Equal(70, cfg.TypingDelay);
        Assert.Equal((15, 35), (cfg.KeyHold.Min, cfg.KeyHold.Max));
        Assert.False(cfg.IdleBetweenActions);
    }

    [Fact]
    public void CarefulPreset_IsSlower()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Careful);
        Assert.Equal(100, cfg.TypingDelay);
        Assert.True(cfg.IdleBetweenActions);
        Assert.Equal((20, 45), (cfg.KeyHold.Min, cfg.KeyHold.Max));
    }

    [Fact]
    public void Overrides_SnakeCase_Keys_Applied()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default, new Dictionary<string, object>
        {
            ["typing_delay"] = 200.0,
            ["mistype_chance"] = 0.5,
        });
        Assert.Equal(200, cfg.TypingDelay);
        Assert.Equal(0.5, cfg.MistypeChance);
    }

    [Fact]
    public void Overrides_PascalCase_Keys_Applied()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default, new Dictionary<string, object>
        {
            ["TypingDelay"] = 250.0,
        });
        Assert.Equal(250, cfg.TypingDelay);
    }

    [Fact]
    public void Overrides_Range_From_Tuple()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default, new Dictionary<string, object>
        {
            ["key_hold"] = (50.0, 100.0),
        });
        Assert.Equal((50, 100), (cfg.KeyHold.Min, cfg.KeyHold.Max));
    }

    [Fact]
    public void Overrides_Range_From_Array()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default, new Dictionary<string, object>
        {
            ["key_hold"] = new object[] { 60, 120 },
        });
        Assert.Equal((60, 120), (cfg.KeyHold.Min, cfg.KeyHold.Max));
    }

    [Fact]
    public void Unknown_Keys_Ignored()
    {
        var cfg = HumanConfigFactory.Resolve(HumanPreset.Default, new Dictionary<string, object>
        {
            ["does_not_exist"] = 5,
        });
        Assert.Equal(70, cfg.TypingDelay); // unchanged
    }

    [Fact]
    public void ParsePreset_CaseInsensitive()
    {
        Assert.Equal(HumanPreset.Careful, HumanConfigFactory.ParsePreset("CAREFUL"));
        Assert.Equal(HumanPreset.Default, HumanConfigFactory.ParsePreset(null));
    }

    [Fact]
    public void ParsePreset_Invalid_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => HumanConfigFactory.ParsePreset("nope"));
    }

    [Fact]
    public void With_DoesNotMutate_Base()
    {
        var baseCfg = new HumanConfig();
        var merged = baseCfg.With(new Dictionary<string, object> { ["typing_delay"] = 999.0 });
        Assert.Equal(70, baseCfg.TypingDelay);
        Assert.Equal(999, merged.TypingDelay);
    }
}
