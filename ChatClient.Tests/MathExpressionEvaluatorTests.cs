using ChatClient.Api.Services.BuiltIn;

namespace ChatClient.Tests;

public class MathExpressionEvaluatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-3+5", 2)]
    [InlineData("10%3", 1)]
    [InlineData("3.5*2", 7)]
    public void Evaluate_ValidExpression_ReturnsExpectedResult(string expression, double expected)
    {
        var actual = MathExpressionEvaluator.Evaluate(expression);

        Assert.Equal(expected, actual, precision: 8);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("2/0")]
    [InlineData("1+")]
    [InlineData("foo")]
    [InlineData("(1+2")]
    public void Evaluate_InvalidExpression_Throws(string expression)
    {
        Assert.Throws<InvalidOperationException>(() => MathExpressionEvaluator.Evaluate(expression));
    }
}
