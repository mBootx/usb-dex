using System.Runtime.InteropServices;
using DexStream.Core.Protocol;
using DexStream.Core.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace DexStream.Media;

/// <summary>
/// Decodes the DeX video stream on the GPU and presents it to a window.
/// </summary>
/// <remarks>
/// <para>
/// Decode and present are deliberately one component. A hardware Media Foundation transform hands
/// back frames as Direct3D 11 textures that it still owns, so the cheapest and lowest-latency thing
/// to do is convert and present each frame immediately and release it, rather than copying it into a
/// queue. There is therefore no frame buffering at all: a frame is presented as soon as it is
/// decoded, which is what keeps the end-to-end figure near the encoder's own latency.
/// </para>
/// <para>
/// Colour conversion, scaling and letterboxing are all done by the D3D11 video processor in a single
/// <c>VideoProcessorBlt</c>. That is a fixed-function block on every modern GPU, so it costs
/// essentially no shader time, and it means the pipeline needs no shaders, vertex buffers or runtime
/// HLSL compilation.
/// </para>
/// <para>
/// Not thread safe: call <see cref="Submit"/>, <see cref="Resize"/> and <see cref="Dispose"/> from
/// one thread. The app runs it on a dedicated decode thread.
/// </para>
/// </remarks>
public sealed class D3D11VideoPipeline : IDisposable
{
    private const int OutputStreamId = 0;
    private const int InputStreamId = 0;

    /// <summary>Media Foundation timestamps are in 100-nanosecond units.</summary>
    private const long HundredNanosecondsPerMicrosecond = 10;

    private readonly ILogger _logger;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Multithread? _multithread;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly IMFDXGIDeviceManager _deviceManager;
    private readonly IMFTransform _transform;
    private readonly bool _allowTearing;

    private IDXGISwapChain1 _swapChain;
    private ID3D11VideoProcessorEnumerator? _processorEnumerator;
    private ID3D11VideoProcessor? _processor;

    private byte[] _codecConfig = [];
    private byte[] _submitBuffer = new byte[256 * 1024];
    private int _processorSourceWidth;
    private int _processorSourceHeight;
    private int _processorTargetWidth;
    private int _processorTargetHeight;
    private ScalingMode _scaling = ScalingMode.Fit;
    private bool _awaitingKeyFrame = true;
    private bool _disposed;

    private D3D11VideoPipeline(
        ILogger logger,
        ID3D11Device device,
        ID3D11DeviceContext context,
        ID3D11Multithread? multithread,
        IDXGISwapChain1 swapChain,
        bool allowTearing,
        ID3D11VideoDevice videoDevice,
        ID3D11VideoContext videoContext,
        IMFDXGIDeviceManager deviceManager,
        IMFTransform transform,
        string decoderName,
        DexCodec codec,
        int sourceWidth,
        int sourceHeight,
        int clientWidth,
        int clientHeight)
    {
        _logger = logger;
        _device = device;
        _context = context;
        _multithread = multithread;
        _swapChain = swapChain;
        _allowTearing = allowTearing;
        _videoDevice = videoDevice;
        _videoContext = videoContext;
        _deviceManager = deviceManager;
        _transform = transform;
        DecoderName = decoderName;
        Codec = codec;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        ClientWidth = clientWidth;
        ClientHeight = clientHeight;
    }

    /// <summary>The transform that is decoding, for the diagnostics pane.</summary>
    public string DecoderName { get; }

    public DexCodec Codec { get; }

    /// <summary>Encoded frame width, as announced by the device agent.</summary>
    public int SourceWidth { get; private set; }

    /// <summary>Encoded frame height, as announced by the device agent.</summary>
    public int SourceHeight { get; private set; }

    public int ClientWidth { get; private set; }

    public int ClientHeight { get; private set; }

    /// <summary>
    /// How the image is fitted into the window. Setting it recomputes the viewport immediately, so a
    /// change applies to the next presented frame rather than waiting for a resize.
    /// </summary>
    public ScalingMode Scaling
    {
        get => _scaling;
        set
        {
            if (_scaling == value)
            {
                return;
            }

            _scaling = value;
            RebuildViewport();
        }
    }

    /// <summary>Where the image sits inside the window, for mapping mouse coordinates.</summary>
    public Viewport Viewport { get; private set; } = Viewport.Empty;

    /// <summary>Frames the decoder emitted but that were dropped before presentation.</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>
    /// Builds the pipeline for a window and a stream format.
    /// </summary>
    /// <param name="windowHandle">The HWND to present into.</param>
    /// <param name="header">The stream header the agent sent, which carries codec and frame size.</param>
    /// <param name="clientWidth">Window client width in physical pixels.</param>
    /// <param name="clientHeight">Window client height in physical pixels.</param>
    /// <exception cref="MediaPipelineException">No usable device or decoder could be created.</exception>
    public static D3D11VideoPipeline Create(
        IntPtr windowHandle,
        DexStreamHeader header,
        int clientWidth,
        int clientHeight,
        ILogger<D3D11VideoPipeline>? logger = null)
    {
        ILogger log = logger ?? NullLogger<D3D11VideoPipeline>.Instance;

        if (windowHandle == IntPtr.Zero)
        {
            throw new MediaPipelineException("A window handle is required to create the video pipeline.");
        }

        ID3D11Device? device = null;
        ID3D11DeviceContext? context = null;
        ID3D11Multithread? multithread = null;
        IDXGISwapChain1? swapChain = null;
        ID3D11VideoDevice? videoDevice = null;
        ID3D11VideoContext? videoContext = null;
        IMFDXGIDeviceManager? deviceManager = null;
        IMFTransform? transform = null;

        try
        {
            (device, context) = CreateDevice();

            // The decoder runs on its own worker threads inside the driver and touches the same
            // device we present with, so the device has to be thread safe. Without this, a hardware
            // MFT either refuses the device or corrupts state under load.
            multithread = device.QueryInterfaceOrNull<ID3D11Multithread>();
            multithread?.SetMultithreadProtected(true);

            if (multithread is null)
            {
                log.LogWarning(
                    "ID3D11Multithread is unavailable, so the decoder and the presenter share an " +
                    "unsynchronised device. Decoding may be unstable.");
            }

            (swapChain, bool allowTearing) = CreateSwapChain(
                device, windowHandle, Math.Max(clientWidth, 1), Math.Max(clientHeight, 1), log);

            videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>()
                ?? throw new MediaPipelineException(
                    "This graphics adapter does not expose ID3D11VideoDevice, so hardware video " +
                    "conversion is unavailable. Update the display driver.");

            videoContext = context.QueryInterfaceOrNull<ID3D11VideoContext>()
                ?? throw new MediaPipelineException(
                    "This graphics adapter does not expose ID3D11VideoContext.");

            MediaFactory.MFStartup(false).CheckError();

            deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            deviceManager.ResetDevice(device).CheckError();

            (transform, string decoderName) = CreateDecoder(header.Codec, log);

            ConfigureDecoder(transform, deviceManager, header, log);

            var pipeline = new D3D11VideoPipeline(
                log, device, context, multithread, swapChain, allowTearing, videoDevice, videoContext,
                deviceManager, transform, decoderName, header.Codec,
                header.Width, header.Height, clientWidth, clientHeight);

            pipeline.RebuildViewport();
            log.LogInformation(
                "Video pipeline ready: {Decoder} decoding {Codec} at {Width}x{Height}, tearing {Tearing}.",
                decoderName,
                header.Codec,
                header.Width,
                header.Height,
                allowTearing ? "allowed" : "not allowed");

            return pipeline;
        }
        catch
        {
            transform?.Dispose();
            deviceManager?.Dispose();
            videoContext?.Dispose();
            videoDevice?.Dispose();
            swapChain?.Dispose();
            multithread?.Dispose();
            context?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    private static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice()
    {
        // VideoSupport is what makes ID3D11VideoDevice available; BgraSupport is needed for the
        // B8G8R8A8 swap chain the desktop compositor prefers.
        DeviceCreationFlags flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

        Result result = D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            flags,
            levels,
            out ID3D11Device? device,
            out ID3D11DeviceContext? context);

        if (result.Failure || device is null || context is null)
        {
            device?.Dispose();
            context?.Dispose();
            throw new MediaPipelineException(
                $"Could not create a Direct3D 11 device with video support (HRESULT 0x{result.Code:X8}). " +
                "A GPU driver supporting Direct3D 11.0 or later is required.");
        }

        return (device, context);
    }

    /// <summary>
    /// Creates a flip-model swap chain, preferring tearing support.
    /// </summary>
    /// <remarks>
    /// Tearing lets a present go out without waiting for the next vertical blank, which removes up to
    /// a full refresh interval of latency. It needs DXGI 1.5 and a supporting driver, and there is no
    /// cheap way to ask, so the creation is simply attempted and retried without the flag.
    /// </remarks>
    private static (IDXGISwapChain1 SwapChain, bool AllowTearing) CreateSwapChain(
        ID3D11Device device,
        IntPtr windowHandle,
        int width,
        int height,
        ILogger logger)
    {
        using IDXGIDevice dxgiDevice = device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        // One frame of queued presents. The default of three trades latency for throughput, which is
        // the wrong trade for interactive streaming.
        using (IDXGIDevice1? device1 = dxgiDevice.QueryInterfaceOrNull<IDXGIDevice1>())
        {
            if (device1 is not null)
            {
                device1.MaximumFrameLatency = 1;
            }
        }

        foreach (bool tearing in new[] { true, false })
        {
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Vortice.DXGI.Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Flags = tearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
            };

            try
            {
                IDXGISwapChain1 swapChain = factory.CreateSwapChainForHwnd(
                    device, windowHandle, description, null, null);

                // Alt+Enter full screen would fight the app's own window handling.
                factory.MakeWindowAssociation(windowHandle, WindowAssociationFlags.IgnoreAltEnter);
                return (swapChain, tearing);
            }
            catch (SharpGenException ex) when (tearing)
            {
                logger.LogDebug(
                    ex, "Tearing is not supported on this adapter; falling back to a vsync-locked swap chain.");
            }
        }

        throw new MediaPipelineException("Could not create a DXGI swap chain for the window.");
    }

    /// <summary>Finds and activates a decoder, preferring a hardware transform.</summary>
    private static (IMFTransform Transform, string Name) CreateDecoder(DexCodec codec, ILogger logger)
    {
        foreach ((uint flags, string kind) in new[]
                 {
                     (VideoDecoderCatalog.PreferredEnumFlags, "hardware"),
                     (VideoDecoderCatalog.SoftwareEnumFlags, "software"),
                 })
        {
            List<IMFActivate> activators = VideoDecoderCatalog.EnumerateActivators(codec, flags);
            try
            {
                foreach (IMFActivate activate in activators)
                {
                    try
                    {
                        IMFTransform transform = activate.ActivateObject<IMFTransform>();
                        string name = $"{codec} {kind} decoder";
                        logger.LogInformation("Activated a {Kind} {Codec} decoder.", kind, codec);
                        return (transform, name);
                    }
                    catch (SharpGenException ex)
                    {
                        logger.LogDebug(ex, "A {Kind} {Codec} decoder failed to activate.", kind, codec);
                    }
                }
            }
            finally
            {
                foreach (IMFActivate activate in activators)
                {
                    activate.Dispose();
                }
            }
        }

        throw new MediaPipelineException(
            $"No Media Foundation decoder for {codec} could be activated. " +
            (codec == DexCodec.H265
                ? "HEVC decoding needs the HEVC Video Extension from the Microsoft Store, or an " +
                  "H.265-capable GPU driver. Choose H.264 in settings to avoid the dependency."
                : "Install or repair the Media Feature Pack for this edition of Windows."));
    }

    /// <summary>Negotiates the decoder's input and output types and hands it the D3D11 device.</summary>
    private static void ConfigureDecoder(
        IMFTransform transform,
        IMFDXGIDeviceManager deviceManager,
        DexStreamHeader header,
        ILogger logger)
    {
        using (IMFAttributes attributes = transform.Attributes)
        {
            if (attributes is not null)
            {
                bool d3d11Aware = TryGetUInt32(attributes, TransformAttributeKeys.D3D11Aware) == 1;
                if (!d3d11Aware)
                {
                    logger.LogWarning(
                        "The decoder does not advertise MF_SA_D3D11_AWARE, so frames will be copied " +
                        "through system memory and latency will be higher.");
                }

                if (TryGetUInt32(attributes, TransformAttributeKeys.TransformAsync) == 1)
                {
                    // An async MFT must be unlocked before it will accept input.
                    attributes.Set(TransformAttributeKeys.TransformAsyncUnlock, 1u);
                }

                // MF_LOW_LATENCY. Vortice does not surface this key, and an unrecognised attribute is
                // simply ignored by a transform, so setting it is safe on every decoder.
                attributes.Set(new Guid("9C27891A-ED7A-40e1-88E8-B22727A024EE"), 1u);
            }
        }

        // Handing the transform our D3D11 device is what makes it decode into GPU textures.
        transform.ProcessMessage(TMessageType.MessageSetD3DManager, (nuint)(nint)deviceManager.NativePointer);

        using IMFMediaType inputType = MediaFactory.MFCreateMediaType();
        inputType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputType.Set(MediaTypeAttributeKeys.Subtype, VideoDecoderCatalog.ToSubtype(header.Codec));
        inputType.Set(MediaTypeAttributeKeys.FrameSize, PackRatio(header.Width, header.Height));
        inputType.Set(MediaTypeAttributeKeys.InterlaceMode, 2u); // MFVideoInterlace_Progressive
        inputType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 0u);
        inputType.Set(MediaTypeAttributeKeys.PixelAspectRatio, PackRatio(1, 1));

        int refreshRateMilliHz = header.RefreshRateMilliHz > 0 ? header.RefreshRateMilliHz : 60_000;
        inputType.Set(MediaTypeAttributeKeys.FrameRate, PackRatio(refreshRateMilliHz, 1000));

        transform.SetInputType(InputStreamId, inputType, 0);

        SelectNv12OutputType(transform);

        transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, 0);
        transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, 0);
    }

    /// <summary>
    /// Picks the decoder's NV12 output type.
    /// </summary>
    /// <remarks>
    /// Every hardware decoder offers NV12, and it is what the video processor converts from most
    /// cheaply. The list has to be walked rather than constructed, because a decoder will reject a
    /// type it did not itself offer.
    /// </remarks>
    private static void SelectNv12OutputType(IMFTransform transform)
    {
        var offered = new List<Guid>();

        for (int index = 0; index < 64; index++)
        {
            IMFMediaType? candidate = null;
            try
            {
                candidate = transform.GetOutputAvailableType(OutputStreamId, index);
            }
            catch (SharpGenException ex) when (ex.ResultCode == MediaFoundationHResults.NoMoreTypes)
            {
                break;
            }

            if (candidate is null)
            {
                break;
            }

            try
            {
                Guid subtype = candidate.GetGUID(MediaTypeAttributeKeys.Subtype);
                offered.Add(subtype);

                if (subtype == VideoFormatGuids.NV12)
                {
                    transform.SetOutputType(OutputStreamId, candidate, 0);
                    return;
                }
            }
            catch (SharpGenException)
            {
                // A type without a readable subtype is not usable; keep looking.
            }
            finally
            {
                candidate.Dispose();
            }
        }

        throw new MediaPipelineException(
            "The decoder does not offer NV12 output, which the presenter requires. Offered subtypes: " +
            (offered.Count == 0 ? "none" : string.Join(", ", offered)));
    }

    /// <summary>Packs a numerator and denominator into the UINT64 form Media Foundation uses.</summary>
    internal static ulong PackRatio(int numerator, int denominator)
        => ((ulong)(uint)numerator << 32) | (uint)denominator;

    private static uint TryGetUInt32(IMFAttributes attributes, Guid key)
    {
        try
        {
            return attributes.GetUInt32(key);
        }
        catch (SharpGenException)
        {
            return 0;
        }
    }

    /// <summary>Tells the pipeline the window changed size, and resizes the swap chain to match.</summary>
    public void Resize(int clientWidth, int clientHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        clientWidth = Math.Max(clientWidth, 1);
        clientHeight = Math.Max(clientHeight, 1);

        if (clientWidth == ClientWidth && clientHeight == ClientHeight)
        {
            return;
        }

        ClientWidth = clientWidth;
        ClientHeight = clientHeight;

        _swapChain.ResizeBuffers(
            0,
            (uint)clientWidth,
            (uint)clientHeight,
            Format.Unknown,
            _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();

        RebuildViewport();
    }

    /// <summary>
    /// Adopts a new stream geometry after the device reported a rotation or resolution change.
    /// </summary>
    public void UpdateSourceGeometry(DexStreamHeader header)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (header.Width == SourceWidth && header.Height == SourceHeight)
        {
            return;
        }

        _logger.LogInformation(
            "Stream geometry changed from {OldWidth}x{OldHeight} to {NewWidth}x{NewHeight}.",
            SourceWidth, SourceHeight, header.Width, header.Height);

        SourceWidth = header.Width;
        SourceHeight = header.Height;
        _codecConfig = [];
        _awaitingKeyFrame = true;

        // The decoder has to renegotiate for the new size; flushing discards anything in flight.
        _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        SelectNv12OutputType(_transform);
        RebuildViewport();
    }

    private void RebuildViewport()
        => Viewport = Viewport.Compute(ClientWidth, ClientHeight, SourceWidth, SourceHeight, Scaling);

    /// <summary>
    /// Feeds one packet from the stream and presents any frame it produces.
    /// </summary>
    /// <param name="payload">The packet body: codec configuration or an encoded access unit.</param>
    /// <param name="presentationTimeUs">The device's capture timestamp, in microseconds.</param>
    /// <param name="isConfig">True when the payload is codec configuration rather than picture data.</param>
    /// <param name="isKeyFrame">True when the access unit is an IDR.</param>
    /// <returns>True when a frame reached the screen.</returns>
    public bool Submit(
        ReadOnlySpan<byte> payload,
        long presentationTimeUs,
        bool isConfig,
        bool isKeyFrame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (isConfig)
        {
            // Parameter sets are kept and re-sent ahead of every key frame. Repeating them is legal
            // and means the decoder can recover from a mid-stream reset without a new config packet.
            _codecConfig = payload.ToArray();
            return false;
        }

        if (payload.IsEmpty)
        {
            return false;
        }

        if (_awaitingKeyFrame)
        {
            if (!isKeyFrame)
            {
                DroppedFrames++;
                return false;
            }

            _awaitingKeyFrame = false;
        }

        int length = BuildSubmitBuffer(payload, isKeyFrame);

        using IMFSample sample = CreateSample(_submitBuffer, length, presentationTimeUs);

        try
        {
            _transform.ProcessInput(InputStreamId, sample, 0);
        }
        catch (SharpGenException ex) when (ex.ResultCode == MediaFoundationHResults.NotAccepting)
        {
            // Output is pending: draining it makes room, then the input is retried once.
            bool presented = DrainOutput();
            _transform.ProcessInput(InputStreamId, sample, 0);
            return DrainOutput() || presented;
        }

        return DrainOutput();
    }

    /// <summary>
    /// Assembles the bytes handed to the decoder, prefixing the stored parameter sets on a key frame.
    /// </summary>
    private int BuildSubmitBuffer(ReadOnlySpan<byte> payload, bool isKeyFrame)
    {
        bool includeConfig = isKeyFrame && _codecConfig.Length > 0;
        int length = payload.Length + (includeConfig ? _codecConfig.Length : 0);

        if (_submitBuffer.Length < length)
        {
            _submitBuffer = new byte[Math.Max(length, _submitBuffer.Length * 2)];
        }

        int offset = 0;
        if (includeConfig)
        {
            _codecConfig.CopyTo(_submitBuffer, 0);
            offset = _codecConfig.Length;
        }

        payload.CopyTo(_submitBuffer.AsSpan(offset));
        return length;
    }

    private static IMFSample CreateSample(byte[] data, int length, long presentationTimeUs)
    {
        IMFSample sample = MediaFactory.MFCreateSample();
        try
        {
            using IMFMediaBuffer buffer = MediaFactory.MFCreateMemoryBuffer(length);
            buffer.Lock(out IntPtr destination, out _, out _);
            try
            {
                Marshal.Copy(data, 0, destination, length);
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.CurrentLength = length;
            sample.AddBuffer(buffer);
            sample.SampleTime = presentationTimeUs * HundredNanosecondsPerMicrosecond;
            return sample;
        }
        catch
        {
            sample.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pulls every frame the decoder has ready and presents each one.
    /// </summary>
    /// <returns>True when at least one frame was presented.</returns>
    private bool DrainOutput()
    {
        bool presented = false;

        while (true)
        {
            var buffers = new OutputDataBuffer
            {
                StreamID = OutputStreamId,
                Sample = null,
                Status = 0,
                Events = null,
            };

            Result result = _transform.ProcessOutput(
                ProcessOutputFlags.None, 1, ref buffers, out _);

            if (result == MediaFoundationHResults.NeedMoreInput)
            {
                return presented;
            }

            if (result == MediaFoundationHResults.StreamChange)
            {
                // The decoder discovered the real stream format, which is normal on the first frames.
                buffers.Sample?.Dispose();
                SelectNv12OutputType(_transform);
                continue;
            }

            if (result.Failure)
            {
                buffers.Sample?.Dispose();
                throw new MediaPipelineException(
                    $"The video decoder failed with HRESULT 0x{result.Code:X8}.");
            }

            IMFSample? sample = buffers.Sample;
            if (sample is null)
            {
                return presented;
            }

            try
            {
                presented |= Present(sample);
            }
            finally
            {
                sample.Dispose();
            }
        }
    }

    /// <summary>Converts a decoded NV12 texture into the back buffer and shows it.</summary>
    private bool Present(IMFSample sample)
    {
        using IMFMediaBuffer buffer = sample.GetBufferByIndex(0);
        using IMFDXGIBuffer? dxgiBuffer = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();

        if (dxgiBuffer is null)
        {
            // A software decoder produced a system-memory frame. Presenting it would need a staging
            // upload per frame, which defeats the point of the pipeline.
            DroppedFrames++;
            _logger.LogWarning(
                "The decoder returned a system-memory frame, which this presenter cannot display. " +
                "Check that a hardware decoder is installed for {Codec}.", Codec);
            return false;
        }

        IntPtr resourcePointer = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
        if (resourcePointer == IntPtr.Zero)
        {
            DroppedFrames++;
            return false;
        }

        using var texture = new ID3D11Texture2D(resourcePointer);
        uint subresource = dxgiBuffer.SubresourceIndex;

        Texture2DDescription description = texture.Description;
        EnsureProcessor((int)description.Width, (int)description.Height);

        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);

        var inputViewDescription = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView
            {
                MipSlice = 0,
                ArraySlice = subresource,
            },
        };

        var outputViewDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
        };

        using ID3D11VideoProcessorInputView inputView = _videoDevice.CreateVideoProcessorInputView(
            texture, _processorEnumerator!, inputViewDescription);
        using ID3D11VideoProcessorOutputView outputView = _videoDevice.CreateVideoProcessorOutputView(
            backBuffer, _processorEnumerator!, outputViewDescription);

        Viewport viewport = Viewport;

        // The decoder's texture is often padded to a macroblock multiple, so the source rectangle is
        // the real picture area rather than the whole texture.
        var sourceRect = new Vortice.RawRect(0, 0, SourceWidth, SourceHeight);
        var destinationRect = new Vortice.RawRect(
            viewport.X, viewport.Y, viewport.X + viewport.Width, viewport.Y + viewport.Height);

        _videoContext.VideoProcessorSetStreamSourceRect(_processor!, 0, true, sourceRect);
        _videoContext.VideoProcessorSetStreamDestRect(_processor!, 0, true, destinationRect);
        _videoContext.VideoProcessorSetOutputTargetRect(
            _processor!, true, new Vortice.RawRect(0, 0, ClientWidth, ClientHeight));

        // Black letterbox bars, so a window wider than the DeX desktop does not show stale pixels.
        _videoContext.VideoProcessorSetOutputBackgroundColor(
            _processor!, false, new VideoColor { Rgba = new VideoColorRgba { R = 0, G = 0, B = 0, A = 1 } });

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = inputView,
        };

        _videoContext.VideoProcessorBlt(_processor!, outputView, 0, 1, [stream]);

        // Sync interval zero with tearing allowed is the lowest-latency present DXGI offers; without
        // tearing support the present is held to the next vertical blank.
        Result present = _allowTearing
            ? _swapChain.Present(0, PresentFlags.AllowTearing)
            : _swapChain.Present(1, PresentFlags.None);

        if (present.Failure)
        {
            throw new MediaPipelineException($"Present failed with HRESULT 0x{present.Code:X8}.");
        }

        return true;
    }

    /// <summary>Creates or recreates the video processor when the source or target size changes.</summary>
    private void EnsureProcessor(int textureWidth, int textureHeight)
    {
        if (_processor is not null
            && _processorSourceWidth == textureWidth
            && _processorSourceHeight == textureHeight
            && _processorTargetWidth == ClientWidth
            && _processorTargetHeight == ClientHeight)
        {
            return;
        }

        _processor?.Dispose();
        _processorEnumerator?.Dispose();
        _processor = null;
        _processorEnumerator = null;

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)textureWidth,
            InputHeight = (uint)textureHeight,
            OutputWidth = (uint)Math.Max(ClientWidth, 1),
            OutputHeight = (uint)Math.Max(ClientHeight, 1),
            InputFrameRate = new Rational(60, 1),
            OutputFrameRate = new Rational(60, 1),
            Usage = VideoUsage.PlaybackNormal,
        };

        _processorEnumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_processorEnumerator, 0);

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);

        _processorSourceWidth = textureWidth;
        _processorSourceHeight = textureHeight;
        _processorTargetWidth = ClientWidth;
        _processorTargetHeight = ClientHeight;
    }

    /// <summary>Asks the decoder to discard everything in flight, after a stream discontinuity.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transform.ProcessMessage(TMessageType.MessageCommandFlush, 0);
        _awaitingKeyFrame = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, 0);
            _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, 0);
        }
        catch (SharpGenException)
        {
            // The transform may already be gone; shutdown proceeds regardless.
        }

        _processor?.Dispose();
        _processorEnumerator?.Dispose();
        _transform.Dispose();
        _deviceManager.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
        _swapChain.Dispose();
        _multithread?.Dispose();
        _context.Dispose();
        _device.Dispose();

        try
        {
            MediaFactory.MFShutdown();
        }
        catch (SharpGenException)
        {
            // Shutting down twice is harmless.
        }
    }
}
