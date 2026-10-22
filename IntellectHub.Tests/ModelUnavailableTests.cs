using IntellectHub.Client;
using Xunit;

namespace IntellectHub.Tests;

/// <summary>
/// Provider answers that name the model itself as the cause become the typed
/// ModelUnavailableException, carrying the replacement the provider named.
/// Everything else — generic NOT_FOUND, authorization errors, unparseable bodies —
/// stays an ordinary HttpRequestException.
/// </summary>
public sealed class ModelUnavailableTests
{
    /// <summary>The exact shape Google's compatibility layer returns for a retired model.</summary>
    private const string GoogleRetired = """
        [{
          "error": {
            "code": 404,
            "message": "This model models/gemini-2.0-flash is no longer available. Please update your code to use models/gemini-3.8-flash for the latest features and improvements. We recommend you to use the Interactions API",
            "status": "NOT_FOUND"
          }
        }]
        """;

    /// <summary>The shape OpenAI returns for an unknown model.</summary>
    private const string OpenAiUnknown = """
        {
          "error": {
            "message": "The model `gpt-4o-does-not-exist` does not exist or you do not have access to it.",
            "type": "invalid_request_error",
            "code": "model_not_found"
          }
        }
        """;

    [Fact]
    public void GoogleRetiredErrorNamesModelAndSuggestedReplacement()
    {
        var ex = ApiClient.ParseModelUnavailable(404, GoogleRetired, "gemini-2.0-flash");

        Assert.NotNull(ex);
        Assert.Equal("gemini-2.0-flash", ex!.Model);
        Assert.Equal("gemini-3.8-flash", ex.Suggestion);
        Assert.True(ex.Message.Contains("no longer available"));
    }

    [Fact]
    public void OpenAiUnknownModelErrorNamesTheModelWithoutSuggestion()
    {
        var ex = ApiClient.ParseModelUnavailable(404, OpenAiUnknown, "gpt-4o-does-not-exist");

        Assert.NotNull(ex);
        Assert.Equal("gpt-4o-does-not-exist", ex!.Model);
        Assert.Null(ex.Suggestion);
    }

    [Fact]
    public void GenericNotFoundStaysAnOrdinaryFailure()
    {
        // Route error from google: body says nothing about a model.
        var ex = ApiClient.ParseModelUnavailable(404, "{}", "gemini-2.0-flash");
        Assert.Null(ex);
    }

    [Fact]
    public void AuthorizationErrorStaysAnOrdinaryFailure()
    {
        const string body = """
            [{
              "error": {
                "code": 400,
                "message": "Missing or invalid Authorization header.",
                "status": "INVALID_ARGUMENT"
              }
            }]
            """;
        var ex = ApiClient.ParseModelUnavailable(400, body, "gemini-2.0-flash");
        Assert.Null(ex);
    }

    [Fact]
    public void UnparseableBodyStaysAnOrdinaryFailure()
    {
        var ex = ApiClient.ParseModelUnavailable(404, "<html>gateway</html>", "ascend");
        Assert.Null(ex);
    }

    [Fact]
    public void RequestedModelIsTheFallbackWhenTheTextNamesNone()
    {
        const string body = """
            {
              "error": {
                "message": "The model does not exist.",
                "type": "invalid_request_error"
              }
            }
            """;
        var ex = ApiClient.ParseModelUnavailable(404, body, "ascend/another-ft-2");
        Assert.NotNull(ex);
        // Full provider paths shorten to bare names, as the picker shows them.
        Assert.Equal(ModelName.Short("ascend/another-ft-2"), ex!.Model);
    }
}
