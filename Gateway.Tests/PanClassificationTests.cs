using Gateway;
using Xunit;

namespace Gateway.Tests;

public sealed class PanClassificationTests
{
    [Theory]
    [InlineData("", PanClassification.NotCandidate)]
    [InlineData("0", PanClassification.NotCandidate)]
    [InlineData("000000000000", PanClassification.NotCandidate)]
    [InlineData("0000000000000", PanClassification.HighConfidence)]
    [InlineData("0000000000001", PanClassification.Candidate)]
    [InlineData("4111111111111111", PanClassification.HighConfidence)]
    [InlineData("4111111111111112", PanClassification.Candidate)]
    [InlineData("0000000000000000000", PanClassification.HighConfidence)]
    [InlineData("0000000000000000001", PanClassification.Candidate)]
    [InlineData("00000000000000000000", PanClassification.NotCandidate)]
    [InlineData("0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0", PanClassification.HighConfidence)]
    [InlineData("0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0-0 ", PanClassification.NotCandidate)]
    [InlineData(" 4111111111111111", PanClassification.NotCandidate)]
    [InlineData("4111111111111111-", PanClassification.NotCandidate)]
    [InlineData("4111--111111111111", PanClassification.NotCandidate)]
    [InlineData("4111 -111111111111", PanClassification.NotCandidate)]
    [InlineData("4111\t111111111111", PanClassification.NotCandidate)]
    [InlineData("４１１１１１１１１１１１１１１１", PanClassification.NotCandidate)]
    [InlineData("PAN 4111111111111111", PanClassification.NotCandidate)]
    public void Whole_value_classification_and_legacy_boolean_parity(string value, object expectedValue)
    {
        var expected = (PanClassification)expectedValue;
        Assert.Equal(expected, SecondaryPanDetector.Classify(value));
        Assert.Equal(expected == PanClassification.HighConfidence, SecondaryPanDetector.IsMatch(value));
    }

    [Fact]
    public void Classification_contract_has_only_three_values_and_returns_enum()
    {
        Assert.Equal(new[] { "NotCandidate", "Candidate", "HighConfidence" }, Enum.GetNames<PanClassification>());
        Assert.Equal(typeof(PanClassification), typeof(SecondaryPanDetector).GetMethod("Classify", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.ReturnType);
    }
}
