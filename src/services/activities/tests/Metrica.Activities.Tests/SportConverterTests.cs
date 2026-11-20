using Metrica.Contracts;

namespace Metrica.Activities.Tests;

/// <summary>
/// Conversione dello sport da/verso il testo minuscolo del database (DEC-005: <c>run|ride|swim|other</c>).
/// Non avviano container.
/// </summary>
[Trait("Category", "Persistence")]
public sealed class SportConverterTests
{
    [Theory]
    [InlineData(Sport.Run, "run")]
    [InlineData(Sport.Ride, "ride")]
    [InlineData(Sport.Swim, "swim")]
    [InlineData(Sport.Other, "other")]
    public void ToProvider_AndBack_RoundTripsEveryValueAsLowercaseText(Sport sport, string text)
    {
        var converter = new SportConverter();

        var stored = (string?)converter.ConvertToProvider(sport);
        var restored = (Sport?)converter.ConvertFromProvider(text);

        Assert.Equal(text, stored);
        Assert.Equal(text, SportConverter.ToText(sport));
        Assert.Equal(sport, restored);
        Assert.Equal(sport, SportConverter.FromText(text));
    }

    [Fact]
    public void EveryDeclaredSportHasADistinctLowercaseText_SoANewValueCannotBeForgotten()
    {
        var texts = Enum.GetValues<Sport>().Select(SportConverter.ToText).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.All(texts, t => Assert.Equal(t.ToLowerInvariant(), t));
        // Stessa rappresentazione dell'evento: il testo coincide con quello del JSON dei contratti.
        foreach (var sport in Enum.GetValues<Sport>())
        {
            Assert.Equal(
                $"\"{SportConverter.ToText(sport)}\"",
                System.Text.Json.JsonSerializer.Serialize(sport, ContractJson.Options));
        }
    }

    [Fact]
    public void ToText_WithAnUndefinedEnumValue_ThrowsAClearError()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => SportConverter.ToText((Sport)99));

        Assert.Contains("99", error.Message);
        Assert.Contains("Sport", error.Message);
    }

    [Theory]
    [InlineData("hike")]
    [InlineData("Run")]      // il database contiene solo minuscole: una maiuscola e' un dato corrotto
    [InlineData("RIDE")]
    [InlineData("")]
    [InlineData(" run")]
    public void FromText_WithAnUnknownValue_ThrowsAClearErrorWithTheOffendingText(string text)
    {
        var error = Assert.Throws<InvalidOperationException>(() => SportConverter.FromText(text));

        Assert.Contains("sport", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"'{text}'", error.Message);
        Assert.Contains("run", error.Message);   // elenca i valori ammessi
    }

    [Fact]
    public void ConvertFromProvider_WithAnUnknownValue_Throws()
    {
        var converter = new SportConverter();

        Assert.Throws<InvalidOperationException>(() => converter.ConvertFromProvider("nuoto"));
    }

    [Fact]
    public void ConvertToProvider_WithAnUndefinedEnumValue_Throws()
    {
        var converter = new SportConverter();

        Assert.Throws<ArgumentOutOfRangeException>(() => converter.ConvertToProvider((Sport)42));
    }
}
