using Microsoft.ML.Tokenizers;

namespace ChatClient.Tests;

public sealed class TiktokenTokenizerTests
{
    [Fact]
    public void CreateForCl100kBase_LoadsTokenizerData()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base", null, null);

        Assert.NotNull(tokenizer);
    }
}
