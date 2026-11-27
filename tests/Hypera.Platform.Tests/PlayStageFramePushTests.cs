#pragma warning disable BL0006
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Hypera.Platform.Domain;
using Hypera.Platform.Services;
using Hypera.Runtime;
using Hypera.Web.UI;
using Hypera.Web.UI.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using Xunit;

namespace Hypera.Platform.Tests;

public sealed class PlayStageFramePushTests
{
    [Fact]
    public async Task PlayStage_DrivesAwaitAndDynamicAssetsViaFramePushAndPostRenderAck_WithoutPolling()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"hypera_playstage_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            await RunPlayStagePushCoreAsync(dataRoot);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dataRoot))
                    {
                        Directory.Delete(dataRoot, recursive: true);
                    }

                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50);
                }
                catch (IOException)
                {
                    // Ignore best-effort temp cleanup lock after test already recorded any failure
                }
            }
        }
    }

    private static async Task RunPlayStagePushCoreAsync(string dataRoot)
    {
        var ackLog = new ConcurrentQueue<string>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHyperaPlatformCore(opts =>
        {
            opts.HostMode = PlatformHostMode.SingleUserLocalAdmin;
            opts.DataRootDirectory = dataRoot;
        });
        services.AddSingleton<IRuntimeSessionManager>(sp => new SpySessionManager(sp.GetRequiredService<RuntimeSessionManager>(), ackLog));
        services.AddHyperaWebUi();
        services.AddSingleton<IJSRuntime, FakeJsRuntime>();
        services.AddSingleton<NavigationManager, FakeNavigationManager>();

        await using ServiceProvider sp = services.BuildServiceProvider();
        await sp.InitializeHyperaPlatformDatabaseAsync();

        IAuthService auth = sp.GetRequiredService<IAuthService>();
        IAppCatalogService catalog = sp.GetRequiredService<IAppCatalogService>();
        IPackagePipelineService pipeline = sp.GetRequiredService<IPackagePipelineService>();
        IRuntimeSessionManager sessions = sp.GetRequiredService<IRuntimeSessionManager>();
        var fakeJs = (FakeJsRuntime)sp.GetRequiredService<IJSRuntime>();

        AppUser admin = await auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await catalog.CreateAppAsync(admin.Id, "PlayStagePushApp");
        GameVersion ver = await catalog.InsertVersionAsync(admin.Id, app.Id, "v1.0");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,PlayStagePushApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                LOCAL = GCREATE(1, 8, 8)
                LOCAL = GCLEAR(1, 0xFF102030)
                LOCAL = SPRITECREATE("DYN", 1, 0, 0, 8, 8)
                HTML_PRINT "<img src='DYN'>"
                PRINTL STEP_0
                AWAIT 30
                CLEARLINE 1
                LOCAL = GCLEAR(1, 0xFF405060)
                PRINTL STEP_1
                AWAIT 30
                CLEARLINE 1
                PRINTL STAGE_READY_INPUT
                INPUT
                LOCAL = GCLEAR(1, 0xFFFFFFFF)
                PRINTL AFTER_USER_INPUT
                INPUT
                QUIT
                """,
        });

        GameLanguageSubmission sub = await pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: ver.Id,
            LanguageCode: "ja",
            UploadedByUserId: admin.Id,
            AllowSourceDownload: false,
            ZipArchiveStream: zip));
        sub = await pipeline.WaitForCompilationAsync(sub.Id);
        Assert.Equal(SubmissionStatus.Published, sub.Status);

        await using var scope = sp.CreateAsyncScope();
        using var renderer = new InteractiveTestRenderer(scope.ServiceProvider);

        string lastRenderedText = string.Empty;
        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.RenderedHtmlUpdated += html =>
        {
            lastRenderedText = html;
            if (html.Contains("STAGE_READY_INPUT", StringComparison.Ordinal))
            {
                readyTcs.TrySetResult(html);
            }
        };

        PlayStageTestHost? host = null;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PlayStageTestHost.AppId)] = app.Id,
            });
            host = await renderer.MountAndRenderAsync<PlayStageTestHost>(parameters);
        });

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            string htmlAtInput = await readyTcs.Task.WaitAsync(timeoutCts.Token);
            Assert.Contains("STAGE_READY_INPUT", htmlAtInput);
        }
        catch (OperationCanceledException)
        {
            renderer.ThrowIfUnhandledException();
            IReadOnlyList<RuntimeSessionSummary> curSessions = await sessions.ListUserSessionsAsync(admin.Id);
            string sessionDesc = curSessions.Count == 0
                ? "none"
                : string.Join("; ", curSessions.Select(s => $"{s.SessionId}:{s.EngineStatus}"));
            RuntimeSessionRenderState? curState = curSessions.Count > 0
                ? await sessions.GetSessionRenderStateAsync(curSessions[0].SessionId, admin.Id, acknowledgeFrame: false)
                : null;
            Assert.Fail($"Timed out waiting for STAGE_READY_INPUT. Sessions=[{sessionDesc}], FrameSeq={curState?.FrameSequence}, Lines=[{string.Join("|", curState?.Scene.VisibleLines.Select(l => l.PlainText) ?? [])}], AckLog=[{string.Join(" | ", ackLog)}], Last rendered text: '{lastRenderedText}'. JS calls: {string.Join(", ", fakeJs.AllCalls)}");
        }

        // Dynamic sprite DYN was fetched once at STEP_0 and re-fetched at STEP_1 after GCLEAR(1)
        int dynAssetRegistrations = fakeJs.AssetRegisterCounts.GetValueOrDefault("sprite:DYN");
        Assert.True(dynAssetRegistrations >= 2, $"Expected dynamic sprite DYN to refresh across GCLEAR frames, got {dynAssetRegistrations}.");

        // Check active session reached WaitingInput cleanly
        IReadOnlyList<RuntimeSessionSummary> userSessions = await sessions.ListUserSessionsAsync(admin.Id);
        RuntimeSessionSummary active = Assert.Single(userSessions);
        Assert.Equal(EngineStepStatus.WaitingInput, active.EngineStatus);

        // Submit input via session & ensure PlayStage receives pushed frame cleanly
        var afterInputTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.RenderedHtmlUpdated += html =>
        {
            if (html.Contains("AFTER_USER_INPUT", StringComparison.Ordinal))
            {
                afterInputTcs.TrySetResult(html);
            }
        };

        RuntimeSessionRenderState? stateBefore = await sessions.GetSessionRenderStateAsync(active.SessionId, admin.Id);
        await sessions.SubmitInputAsync(active.SessionId, admin.Id, new InputResponse(stateBefore!.PendingInput!.GenerationId, IntValue: 1));
        string htmlAfterInput = await afterInputTcs.Task.WaitAsync(timeoutCts.Token);
        Assert.Contains("AFTER_USER_INPUT", htmlAfterInput);

        // Undo via session -> PlayStage receives pushed Undo frame and restores STAGE_READY_INPUT cleanly
        var undoTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.RenderedHtmlUpdated += html =>
        {
            if (html.Contains("STAGE_READY_INPUT", StringComparison.Ordinal) &&
                !html.Contains("AFTER_USER_INPUT", StringComparison.Ordinal))
            {
                undoTcs.TrySetResult(html);
            }
        };

        await sessions.UndoSessionAsync(active.SessionId, admin.Id);
        string htmlAfterUndo = await undoTcs.Task.WaitAsync(timeoutCts.Token);
        Assert.Contains("STAGE_READY_INPUT", htmlAfterUndo);
        Assert.DoesNotContain("AFTER_USER_INPUT", htmlAfterUndo);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        });

        renderer.ThrowIfUnhandledException();
    }

    [Fact]
    public async Task PlayStage_TimedWaitTimeoutTimer_AutomaticallyResumesEngineToNextPrompt()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"hypera_playstage_twait_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataRoot);

        try
        {
            await RunTimedWaitCoreAsync(dataRoot);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (Directory.Exists(dataRoot))
                    {
                        Directory.Delete(dataRoot, recursive: true);
                    }

                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(50);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static async Task RunTimedWaitCoreAsync(string dataRoot)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHyperaPlatformCore(opts =>
        {
            opts.HostMode = PlatformHostMode.SingleUserLocalAdmin;
            opts.DataRootDirectory = dataRoot;
        });
        services.AddHyperaWebUi();
        services.AddSingleton<IJSRuntime, FakeJsRuntime>();
        services.AddSingleton<NavigationManager, FakeNavigationManager>();

        await using ServiceProvider sp = services.BuildServiceProvider();
        await sp.InitializeHyperaPlatformDatabaseAsync();

        IAuthService auth = sp.GetRequiredService<IAuthService>();
        IAppCatalogService catalog = sp.GetRequiredService<IAppCatalogService>();
        IPackagePipelineService pipeline = sp.GetRequiredService<IPackagePipelineService>();

        AppUser admin = await auth.GetOrEnsureLocalAdminAsync();
        GameApp app = await catalog.CreateAppAsync(admin.Id, "PlayStageTwaitApp");
        GameVersion ver = await catalog.InsertVersionAsync(admin.Id, app.Id, "v1.0");

        using MemoryStream zip = PlatformTestWorkspace.CreateZipStream(new Dictionary<string, string>
        {
            ["CSV/GameBase.csv"] = "タイトル,PlayStageTwaitApp\nバージョン,1000\n",
            ["ERB/SYSTEM.ERB"] =
                """
                @SYSTEM_TITLE
                PRINTL BEFORE_TWAIT
                TWAIT 80, 1
                PRINTL AFTER_TWAIT_OK
                INPUT
                QUIT
                """,
        });

        GameLanguageSubmission sub = await pipeline.SubmitZipPackageAsync(new PackageUploadRequest(
            VersionId: ver.Id,
            LanguageCode: "ja",
            UploadedByUserId: admin.Id,
            AllowSourceDownload: false,
            ZipArchiveStream: zip));
        sub = await pipeline.WaitForCompilationAsync(sub.Id);
        Assert.Equal(SubmissionStatus.Published, sub.Status);

        await using var scope = sp.CreateAsyncScope();
        using var renderer = new InteractiveTestRenderer(scope.ServiceProvider);

        var doneTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        renderer.RenderedHtmlUpdated += html =>
        {
            if (html.Contains("AFTER_TWAIT_OK", StringComparison.Ordinal))
            {
                doneTcs.TrySetResult(html);
            }
        };

        PlayStageTestHost? host = null;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PlayStageTestHost.AppId)] = app.Id,
            });
            host = await renderer.MountAndRenderAsync<PlayStageTestHost>(parameters);
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        string html = await doneTcs.Task.WaitAsync(cts.Token);
        Assert.Contains("AFTER_TWAIT_OK", html);

        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            if (host is not null)
            {
                await host.DisposeAsync();
            }
        });

        renderer.ThrowIfUnhandledException();
    }

    private sealed class PlayStageTestHost : ComponentBase, IAsyncDisposable
    {
        [Parameter]
        public Guid AppId { get; set; }

        private PlayStage? _stage;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<MudBlazor.MudPopoverProvider>(0);
            builder.CloseComponent();

            builder.OpenComponent<PlayStage>(1);
            builder.AddAttribute(2, nameof(PlayStage.AppId), AppId);
            builder.AddComponentReferenceCapture(3, c => _stage = (PlayStage)c);
            builder.CloseComponent();
        }

        public async ValueTask DisposeAsync()
        {
            if (_stage is not null)
            {
                await _stage.DisposeAsync();
            }
        }
    }

    private sealed class InteractiveTestRenderer : Renderer
    {
        private readonly List<int> _mountedRoots = [];
        private ExceptionDispatchInfo? _unhandledException;

        public InteractiveTestRenderer(IServiceProvider serviceProvider)
            : base(serviceProvider, NullLoggerFactory.Instance)
        {
        }

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public event Action<string>? RenderedHtmlUpdated;

        public async Task<TComponent> MountAndRenderAsync<TComponent>(ParameterView parameters)
            where TComponent : IComponent
        {
            IComponent instance = InstantiateComponent(typeof(TComponent));
            int rootId = AssignRootComponentId(instance);
            _mountedRoots.Add(rootId);
            await RenderRootComponentAsync(rootId, parameters);
            return (TComponent)instance;
        }

        public void ThrowIfUnhandledException() => _unhandledException?.Throw();

        protected override void HandleException(Exception exception)
        {
            _unhandledException ??= ExceptionDispatchInfo.Capture(exception);
        }

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
        {
            if (_mountedRoots.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                AppendFramesText(renderBatch, _mountedRoots[0], sb);
                RenderedHtmlUpdated?.Invoke(sb.ToString());
            }

            return Task.CompletedTask;
        }

        private void AppendFramesText(in RenderBatch batch, int componentId, System.Text.StringBuilder sb)
        {
            ArrayRange<RenderTreeFrame> frames = GetCurrentRenderTreeFrames(componentId);
            for (int i = 0; i < frames.Count; i++)
            {
                ref RenderTreeFrame frame = ref frames.Array[i];
                if (frame.FrameType is RenderTreeFrameType.Text or RenderTreeFrameType.Markup)
                {
                    sb.Append(frame.TextContent);
                }
                else if (frame.FrameType == RenderTreeFrameType.Component)
                {
                    AppendFramesText(batch, frame.ComponentId, sb);
                }
            }
        }
    }

    private sealed class FakeJsRuntime : IJSRuntime
    {
        private int _blobSeq;
        public ConcurrentQueue<string> AllCalls { get; } = new();
        public ConcurrentDictionary<string, int> AssetRegisterCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            AllCalls.Enqueue(identifier);
            if (string.Equals(identifier, "hyperaPlayer.registerAssetFromStream", StringComparison.Ordinal) &&
                args is { Length: > 0 } &&
                args[0] is string key)
            {
                AssetRegisterCounts.AddOrUpdate(key, 1, static (_, c) => c + 1);
                string url = $"blob:test/{Interlocked.Increment(ref _blobSeq)}";
                return ValueTask.FromResult((TValue)(object)url);
            }

            if (typeof(TValue) == typeof(bool))
            {
                return ValueTask.FromResult((TValue)(object)true);
            }

            if (typeof(TValue) == typeof(IJSVoidResult))
            {
                return ValueTask.FromResult<TValue>(default!);
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class FakeNavigationManager : NavigationManager
    {
        public FakeNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }

        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class SpySessionManager(IRuntimeSessionManager inner, ConcurrentQueue<string> log) : IRuntimeSessionManager
    {
        public Task<RuntimeSessionSnapshot> StartSessionAsync(Guid userId, Guid appId, Guid? explicitVersionId = null, string? explicitLanguageCode = null, CancellationToken cancellationToken = default)
        {
            log.Enqueue("StartSession");
            return inner.StartSessionAsync(userId, appId, explicitVersionId, explicitLanguageCode, cancellationToken);
        }
        public Task<RuntimeSessionSnapshot> RestartSessionAsync(Guid userId, Guid appId, Guid? explicitVersionId = null, string? explicitLanguageCode = null, CancellationToken cancellationToken = default) => inner.RestartSessionAsync(userId, appId, explicitVersionId, explicitLanguageCode, cancellationToken);
        public Task<RuntimeSessionSnapshot?> GetSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default) => inner.GetSessionAsync(sessionId, userId, cancellationToken);
        public async Task<RuntimeSessionRenderState?> GetSessionRenderStateAsync(Guid sessionId, Guid userId, long elapsedAnimationMs = 0, long afterAudioEventSequence = 0, bool acknowledgeFrame = true, CancellationToken cancellationToken = default)
        {
            var r = await inner.GetSessionRenderStateAsync(sessionId, userId, elapsedAnimationMs, afterAudioEventSequence, acknowledgeFrame, cancellationToken);
            log.Enqueue($"GetState(ack={acknowledgeFrame},seq={r?.FrameSequence},status={r?.Summary.EngineStatus})");
            return r;
        }
        public ISessionFrameSubscription SubscribeSessionFrames(Guid sessionId, Guid userId, bool requireExplicitAck = false)
        {
            log.Enqueue($"Subscribe(ack={requireExplicitAck})");
            return inner.SubscribeSessionFrames(sessionId, userId, requireExplicitAck);
        }
        public Task AcknowledgeSessionFrameAsync(Guid sessionId, Guid userId, long frameSequence, CancellationToken cancellationToken = default)
        {
            log.Enqueue($"Ack(seq={frameSequence})");
            return inner.AcknowledgeSessionFrameAsync(sessionId, userId, frameSequence, cancellationToken);
        }
        public Task<SessionAssetPayload?> ReadSessionAssetAsync(Guid sessionId, Guid userId, string assetKey, long elapsedAnimationMs = 0, CancellationToken cancellationToken = default) => inner.ReadSessionAssetAsync(sessionId, userId, assetKey, elapsedAnimationMs, cancellationToken);
        public Task<SessionHitTestActionResult> HitTestAndClickAsync(Guid sessionId, Guid userId, float logicalX, float logicalY, int mouseButton = 0, bool submitIfInteractive = true, CancellationToken cancellationToken = default) => inner.HitTestAndClickAsync(sessionId, userId, logicalX, logicalY, mouseButton, submitIfInteractive, cancellationToken);
        public Task<IReadOnlyList<RuntimeSessionSummary>> ListUserSessionsAsync(Guid userId, CancellationToken cancellationToken = default) => inner.ListUserSessionsAsync(userId, cancellationToken);
        public Task<RuntimeSessionSnapshot> SubmitInputAsync(Guid sessionId, Guid userId, InputResponse input, CancellationToken cancellationToken = default) => inner.SubmitInputAsync(sessionId, userId, input, cancellationToken);
        public Task<RuntimeSessionSnapshot> UndoSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default) => inner.UndoSessionAsync(sessionId, userId, cancellationToken);
        public Task<RuntimeSessionSnapshot> StepYieldedSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default) => inner.StepYieldedSessionAsync(sessionId, userId, cancellationToken);
        public Task<RuntimeSessionSnapshot> ScrollSessionAsync(Guid sessionId, Guid userId, int deltaLines, bool scrollToBottom = false, CancellationToken cancellationToken = default) => inner.ScrollSessionAsync(sessionId, userId, deltaLines, scrollToBottom, cancellationToken);
        public Task<byte[]> RenderFramePngAsync(Guid sessionId, Guid userId, int? physicalWidth = null, int? physicalHeight = null, CancellationToken cancellationToken = default) => inner.RenderFramePngAsync(sessionId, userId, physicalWidth, physicalHeight, cancellationToken);
        public Task<bool> StopSessionAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default) => inner.StopSessionAsync(sessionId, userId, cancellationToken);
        public Task<int> StopSessionsForAppAsync(Guid appId, CancellationToken cancellationToken = default) => inner.StopSessionsForAppAsync(appId, cancellationToken);
        public Task<int> EvictIdleSessionsAsync(TimeSpan idleTimeout, CancellationToken cancellationToken = default) => inner.EvictIdleSessionsAsync(idleTimeout, cancellationToken);
    }
}
