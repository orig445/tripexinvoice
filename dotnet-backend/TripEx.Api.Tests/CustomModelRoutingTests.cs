using TripEx.Api.Services;
using Xunit;

namespace TripEx.Api.Tests;

/// <summary>
/// Tests for OracleAiService.ResolveChatTarget — the routing logic that decides
/// whether a chat call goes to Milo's fine-tuned custom model or stays on the
/// default (Gemini) model/endpoint. Both the call-site opt-in (allowCustomModel)
/// and the ops-controlled config flag (useCustomModelFlag) must be true, and both
/// custom values must actually be configured, or it falls back to the default —
/// this is what makes Oracle:UseCustomModel a safe, instant, code-free rollback.
/// </summary>
public class CustomModelRoutingTests
{
    private const string DefaultEndpoint = "https://inference.generativeai.us-chicago-1.oci.oraclecloud.com/20231130/actions/v1/chat/completions";
    private const string DefaultModel = "google.gemini-2.5-flash";
    private const string CustomEndpoint = "https://custom-hosting-cluster.generativeai.us-chicago-1.oci.oraclecloud.com/predict";
    private const string CustomModelId = "ocid1.generativeaimodel.oc1..customfinetuned";

    [Fact]
    public void AllConditionsMet_RoutesToCustomModel()
    {
        var (endpoint, model) = OracleAiService.ResolveChatTarget(
            allowCustomModel: true,
            useCustomModelFlag: true,
            customEndpoint: CustomEndpoint,
            customModelId: CustomModelId,
            defaultEndpoint: DefaultEndpoint,
            defaultModel: DefaultModel);

        Assert.Equal(CustomEndpoint, endpoint);
        Assert.Equal(CustomModelId, model);
    }

    [Fact]
    public void CallSiteDoesNotOptIn_StaysOnDefault_EvenIfFlagOn()
    {
        // This is the OCR/invoice-scan case: allowCustomModel is never passed as true
        // there, so it must never reach the fine-tuned (vision-incapable) model no
        // matter how Oracle:UseCustomModel is configured.
        var (endpoint, model) = OracleAiService.ResolveChatTarget(
            allowCustomModel: false,
            useCustomModelFlag: true,
            customEndpoint: CustomEndpoint,
            customModelId: CustomModelId,
            defaultEndpoint: DefaultEndpoint,
            defaultModel: DefaultModel);

        Assert.Equal(DefaultEndpoint, endpoint);
        Assert.Equal(DefaultModel, model);
    }

    [Fact]
    public void FlagOff_StaysOnDefault_EvenIfCallSiteOptsIn()
    {
        // This is the rollback case: Milo's call site always passes allowCustomModel:
        // true, so Oracle:UseCustomModel=false must be the only thing needed to
        // revert production to Gemini.
        var (endpoint, model) = OracleAiService.ResolveChatTarget(
            allowCustomModel: true,
            useCustomModelFlag: false,
            customEndpoint: CustomEndpoint,
            customModelId: CustomModelId,
            defaultEndpoint: DefaultEndpoint,
            defaultModel: DefaultModel);

        Assert.Equal(DefaultEndpoint, endpoint);
        Assert.Equal(DefaultModel, model);
    }

    [Theory]
    [InlineData(null, CustomModelId)]
    [InlineData("", CustomModelId)]
    [InlineData("   ", CustomModelId)]
    [InlineData(CustomEndpoint, null)]
    [InlineData(CustomEndpoint, "")]
    [InlineData(CustomEndpoint, "   ")]
    public void MissingOrBlankCustomValue_StaysOnDefault(string? customEndpoint, string? customModelId)
    {
        var (endpoint, model) = OracleAiService.ResolveChatTarget(
            allowCustomModel: true,
            useCustomModelFlag: true,
            customEndpoint: customEndpoint,
            customModelId: customModelId,
            defaultEndpoint: DefaultEndpoint,
            defaultModel: DefaultModel);

        Assert.Equal(DefaultEndpoint, endpoint);
        Assert.Equal(DefaultModel, model);
    }

    [Fact]
    public void NothingConfigured_DefaultBehaviorUnchanged()
    {
        // Simulates a fresh deploy that hasn't touched the new config keys at all —
        // must behave exactly like before this feature existed.
        var (endpoint, model) = OracleAiService.ResolveChatTarget(
            allowCustomModel: true,
            useCustomModelFlag: false,
            customEndpoint: null,
            customModelId: null,
            defaultEndpoint: DefaultEndpoint,
            defaultModel: DefaultModel);

        Assert.Equal(DefaultEndpoint, endpoint);
        Assert.Equal(DefaultModel, model);
    }
}
