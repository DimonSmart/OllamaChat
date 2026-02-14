using System.Globalization;

namespace ChatClient.Api.Services.BuiltIn;

public static class MathExpressionEvaluator
{
    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new InvalidOperationException("Expression cannot be empty.");

        var parser = new Parser(expression);
        var value = parser.ParseExpression();
        parser.SkipWhitespace();

        if (!parser.IsAtEnd)
            throw new InvalidOperationException($"Unexpected token at position {parser.Position + 1}.");

        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException("Expression evaluation returned a non-finite result.");

        return value;
    }

    private sealed class Parser(string input)
    {
        private readonly string _input = input;
        private int _position;

        public int Position => _position;
        public bool IsAtEnd => _position >= _input.Length;

        public double ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhitespace();
                if (Match('+'))
                {
                    value += ParseTerm();
                    continue;
                }

                if (Match('-'))
                {
                    value -= ParseTerm();
                    continue;
                }

                return value;
            }
        }

        private double ParseTerm()
        {
            var value = ParsePower();
            while (true)
            {
                SkipWhitespace();
                if (Match('*'))
                {
                    value *= ParsePower();
                    continue;
                }

                if (Match('/'))
                {
                    var divisor = ParsePower();
                    if (Math.Abs(divisor) <= double.Epsilon)
                        throw new InvalidOperationException("Division by zero.");

                    value /= divisor;
                    continue;
                }

                if (Match('%'))
                {
                    var divisor = ParsePower();
                    if (Math.Abs(divisor) <= double.Epsilon)
                        throw new InvalidOperationException("Division by zero.");

                    value %= divisor;
                    continue;
                }

                return value;
            }
        }

        private double ParsePower()
        {
            var value = ParseUnary();
            SkipWhitespace();

            if (!Match('^'))
                return value;

            var exponent = ParsePower();
            return Math.Pow(value, exponent);
        }

        private double ParseUnary()
        {
            SkipWhitespace();
            if (Match('+'))
                return ParseUnary();
            if (Match('-'))
                return -ParseUnary();

            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();
            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhitespace();
                if (!Match(')'))
                    throw new InvalidOperationException("Missing closing parenthesis.");

                return value;
            }

            return ParseNumber();
        }

        private double ParseNumber()
        {
            SkipWhitespace();
            var start = _position;
            var hasDigits = false;
            var hasDot = false;

            while (!IsAtEnd)
            {
                var ch = _input[_position];
                if (char.IsDigit(ch))
                {
                    hasDigits = true;
                    _position++;
                    continue;
                }

                if (ch == '.')
                {
                    if (hasDot)
                        break;

                    hasDot = true;
                    _position++;
                    continue;
                }

                break;
            }

            if (!hasDigits)
                throw new InvalidOperationException($"Expected number at position {start + 1}.");

            var token = _input[start.._position];
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new InvalidOperationException($"Invalid number '{token}'.");

            return value;
        }

        public void SkipWhitespace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_input[_position]))
            {
                _position++;
            }
        }

        private bool Match(char expected)
        {
            if (IsAtEnd || _input[_position] != expected)
                return false;

            _position++;
            return true;
        }
    }
}
