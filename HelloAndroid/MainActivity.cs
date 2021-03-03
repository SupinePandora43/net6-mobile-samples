using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Veldrid;
using Veldrid.SPIRV;

namespace HelloAndroid
{
    public class VeldridSurfaceView : SurfaceView, ISurfaceHolderCallback
    {
        private readonly GraphicsBackend _backend;
        protected GraphicsDeviceOptions DeviceOptions { get; }
        private bool _surfaceDestroyed;
        private bool _paused;
        private bool _enabled;
        private bool _needsResize;
        private bool _surfaceCreated;

        public GraphicsDevice GraphicsDevice { get; protected set; }
        public Swapchain MainSwapchain { get; protected set; }

        public event Action Rendering;
        public event Action DeviceCreated;
        public event Action DeviceDisposed;
        public event Action Resized;

        public VeldridSurfaceView(Context context, GraphicsBackend backend)
            : this(context, backend, new GraphicsDeviceOptions())
        {
        }

        public VeldridSurfaceView(Context context, GraphicsBackend backend, GraphicsDeviceOptions deviceOptions) : base(context)
        {
            if (!(backend == GraphicsBackend.Vulkan || backend == GraphicsBackend.OpenGLES))
            {
                throw new NotSupportedException($"{backend} is not supported on Android.");
            }

            _backend = backend;
            DeviceOptions = deviceOptions;
            Holder.AddCallback(this);
        }

        public void Disable()
        {
            _enabled = false;
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            bool deviceCreated = false;
            if (_backend == GraphicsBackend.Vulkan)
            {
                if (GraphicsDevice == null)
                {
                    GraphicsDevice = GraphicsDevice.CreateVulkan(DeviceOptions);
                    deviceCreated = true;
                }

                SwapchainSource ss = SwapchainSource.CreateAndroidSurface(holder.Surface.Handle, JNIEnv.Handle);
                SwapchainDescription sd = new SwapchainDescription(
                    ss,
                    (uint)Width,
                    (uint)Height,
                    DeviceOptions.SwapchainDepthFormat,
                    DeviceOptions.SyncToVerticalBlank);
                MainSwapchain = GraphicsDevice.ResourceFactory.CreateSwapchain(sd);
            }
            else
            {
                SwapchainSource ss = SwapchainSource.CreateAndroidSurface(holder.Surface.Handle, JNIEnv.Handle);
                SwapchainDescription sd = new SwapchainDescription(
                    ss,
                    (uint)Width,
                    (uint)Height,
                    DeviceOptions.SwapchainDepthFormat,
                    DeviceOptions.SyncToVerticalBlank);
                GraphicsDevice = GraphicsDevice.CreateOpenGLES(DeviceOptions, sd);
                MainSwapchain = GraphicsDevice.MainSwapchain;
                deviceCreated = true;
            }

            if (deviceCreated)
            {
                DeviceCreated?.Invoke();
            }

            _surfaceCreated = true;
        }

        public void RunContinuousRenderLoop()
        {
            Task.Factory.StartNew(() => RenderLoop(), TaskCreationOptions.LongRunning);
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            _surfaceDestroyed = true;
        }

        public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Format format, int width, int height)
        {
            _needsResize = true;
            _surfaceDestroyed = false;
        }

        private void RenderLoop()
        {
            _enabled = true;
            while (_enabled)
            {
                try
                {
                    if (_paused || !_surfaceCreated) { continue; }

                    if (_surfaceDestroyed)
                    {
                        HandleSurfaceDestroyed();
                        continue;
                    }

                    if (_needsResize)
                    {
                        _needsResize = false;
                        MainSwapchain.Resize((uint)Width, (uint)Height);
                        Resized?.Invoke();
                    }

                    if (GraphicsDevice != null)
                    {
                        Rendering?.Invoke();
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine("Encountered an error while rendering: " + e);
                    throw;
                }
            }
        }

        private void HandleSurfaceDestroyed()
        {
            if (_backend == GraphicsBackend.Vulkan)
            {
                MainSwapchain.Dispose();
                MainSwapchain = null;
            }
            else
            {
                GraphicsDevice.Dispose();
                GraphicsDevice = null;
                MainSwapchain = null;
                DeviceDisposed?.Invoke();
            }
        }

        public void OnPause()
        {
            _paused = true;
        }

        public void OnResume()
        {
            _paused = false;
        }
    }
    public interface ApplicationWindow
    {
        event Action<float> Rendering;
        event Action<GraphicsDevice, ResourceFactory, Swapchain> GraphicsDeviceCreated;
        event Action GraphicsDeviceDestroyed;
        event Action Resized;
        event Action<KeyEvent> KeyPressed;

        uint Width { get; }
        uint Height { get; }

        void Run();
    }
    public class AndroidApplicationWindow : ApplicationWindow
    {
        // This is supposed to be a DisposeCollectorResourceFactory but it crashes mono
        private ResourceFactory _disposeFactory;
        public readonly System.Diagnostics.Stopwatch _sw;
        private double _previousSeconds;
        private VeldridSurfaceView _view;

        public event Action<GraphicsDevice, ResourceFactory, Swapchain> GraphicsDeviceCreated;
        public event Action GraphicsDeviceDestroyed;

        public uint Width => (uint)_view.Width;
        public uint Height => (uint)_view.Height;

        public event Action<float> Rendering;
        public event Action Resized;
        public event Action<KeyEvent> KeyPressed;

        public AndroidApplicationWindow(Context context, VeldridSurfaceView view)
        {
            _view = view;
            _view.Rendering += OnViewRendering;
            _view.DeviceCreated += OnViewCreatedDevice;
            _view.Resized += OnViewResized;
            _view.DeviceDisposed += OnViewDeviceDisposed;
            _sw = System.Diagnostics.Stopwatch.StartNew();
        }

        private void OnViewDeviceDisposed() => GraphicsDeviceDestroyed?.Invoke();

        private void OnViewResized() => Resized?.Invoke();

        private void OnViewCreatedDevice()
        {
            _disposeFactory = _view.GraphicsDevice.ResourceFactory;
            GraphicsDeviceCreated?.Invoke(_view.GraphicsDevice, _disposeFactory, _view.MainSwapchain);
            Resized?.Invoke();
        }

        private void OnViewRendering()
        {
            double newSeconds = _sw.Elapsed.TotalSeconds;
            double deltaSeconds = newSeconds - _previousSeconds;
            _previousSeconds = newSeconds;
            Rendering?.Invoke((float)deltaSeconds);
        }

        public void Run()
        {
            _view.RunContinuousRenderLoop();
        }
    }

    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        private const string VertexCode = @"
#version 450
layout(location = 0) in vec2 Position;
layout(location = 1) in vec4 Color;
layout(location = 0) out vec4 fsin_Color;
void main()
{
    gl_Position = vec4(Position, 0, 1);
    fsin_Color = Color;
}";

        private const string FragmentCode = @"
#version 450
layout(location = 0) in vec4 fsin_Color;
layout(location = 0) out vec4 fsout_Color;
void main()
{
    fsout_Color = fsin_Color;
}";
        private VeldridSurfaceView _view;
        private AndroidApplicationWindow _window;

        private GraphicsDevice _graphicsDevice;
        private CommandList _commandList;
        ResourceFactory factory;

        Pipeline _pipeline;

        private DeviceBuffer _vertexBuffer;
        private DeviceBuffer _indexBuffer;
        private Veldrid.Shader[] _shaders;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            // Set our view from the "main" layout resource
            /*SetContentView(Resource.Layout.activity_main);

            TextView textView1 = FindViewById<TextView>(Resource.Id.textView1);
            
            bool vulkan = GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan);

            bool isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

            string desc = RuntimeInformation.OSDescription;

            OperatingSystem os = System.Environment.OSVersion;

            bool isAndroid = OperatingSystem.IsAndroid();

            textView1.SetText(isAndroid.ToString(), null);
            */

            GraphicsDeviceOptions options = new()
            {
                PreferDepthRangeZeroToOne = true,
                PreferStandardClipSpaceYDirection = true,
                ResourceBindingModel = ResourceBindingModel.Improved
            };

            GraphicsBackend backend = GraphicsDevice.IsBackendSupported(GraphicsBackend.Vulkan)
                ? GraphicsBackend.Vulkan
                : GraphicsBackend.OpenGLES;

            if(backend != GraphicsBackend.Vulkan)
            {
                Console.Error.WriteLine("vk threw an error\nOr device doesn't support Vulkan");
            }

            _view = new VeldridSurfaceView(this, backend, options);
            _window = new AndroidApplicationWindow(this, _view);
            _window.GraphicsDeviceCreated += CreateResources;
            _window.GraphicsDeviceCreated += (g, r, s) => _window.Run();
            
            SetContentView(_view);

            _window.Rendering += Draw;
        }
        Swapchain swapchain;
        private void CreateResources(GraphicsDevice _graphicsDevice, ResourceFactory factory, Swapchain sc)
        {
            this._graphicsDevice = _graphicsDevice;
            this.swapchain = sc;
            VertexPositionColor[] quadVertices =
            {
                new VertexPositionColor(new Vector2(-.75f, .75f), RgbaFloat.Red),
                new VertexPositionColor(new Vector2(.75f, .75f), RgbaFloat.Green),
                new VertexPositionColor(new Vector2(-.75f, -.75f), RgbaFloat.Blue),
                new VertexPositionColor(new Vector2(.75f, -.75f), RgbaFloat.Yellow)
            };
            BufferDescription vbDescription = new BufferDescription(
                4 * VertexPositionColor.SizeInBytes,
                BufferUsage.VertexBuffer);
            _vertexBuffer = factory.CreateBuffer(vbDescription);
            _graphicsDevice.UpdateBuffer(_vertexBuffer, 0, quadVertices);

            ushort[] quadIndices = { 0, 1, 2, 3 };
            BufferDescription ibDescription = new BufferDescription(
                4 * sizeof(ushort),
                BufferUsage.IndexBuffer);
            _indexBuffer = factory.CreateBuffer(ibDescription);
            _graphicsDevice.UpdateBuffer(_indexBuffer, 0, quadIndices);

            VertexLayoutDescription vertexLayout = new VertexLayoutDescription(
                new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4));

            ShaderDescription vertexShaderDesc = new ShaderDescription(
                ShaderStages.Vertex,
                Encoding.UTF8.GetBytes(VertexCode),
                "main");
            ShaderDescription fragmentShaderDesc = new ShaderDescription(
                ShaderStages.Fragment,
                Encoding.UTF8.GetBytes(FragmentCode),
                "main");

            _shaders = factory.CreateFromSpirv(vertexShaderDesc, fragmentShaderDesc);

            // Create pipeline
            GraphicsPipelineDescription pipelineDescription = new GraphicsPipelineDescription();
            pipelineDescription.BlendState = BlendStateDescription.SingleOverrideBlend;
            pipelineDescription.DepthStencilState = new DepthStencilStateDescription(
                depthTestEnabled: true,
                depthWriteEnabled: true,
                comparisonKind: ComparisonKind.LessEqual);
            pipelineDescription.RasterizerState = new RasterizerStateDescription(
                cullMode: FaceCullMode.Back,
                fillMode: PolygonFillMode.Solid,
                frontFace: FrontFace.Clockwise,
                depthClipEnabled: true,
                scissorTestEnabled: false);
            pipelineDescription.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
            pipelineDescription.ResourceLayouts = System.Array.Empty<ResourceLayout>();
            pipelineDescription.ShaderSet = new ShaderSetDescription(
                vertexLayouts: new VertexLayoutDescription[] { vertexLayout },
                shaders: _shaders);
            pipelineDescription.Outputs = _graphicsDevice.SwapchainFramebuffer.OutputDescription;

            _pipeline = factory.CreateGraphicsPipeline(pipelineDescription);

            _commandList = factory.CreateCommandList();
        }
        private void Draw(float delta)
        {
            float time = (float)_window._sw.Elapsed.TotalSeconds;
            // Begin() must be called before commands can be issued.
            _commandList.Begin();
            
            // We want to render directly to the output window.
            _commandList.SetFramebuffer(_view.MainSwapchain.Framebuffer);
            _commandList.ClearColorTarget(0, new RgbaFloat(MathF.Sin(time), MathF.Cos(time), 255,255));

            // Set all relevant state to draw our quad.
            _commandList.SetVertexBuffer(0, _vertexBuffer);
            _commandList.SetIndexBuffer(_indexBuffer, IndexFormat.UInt16);
            _commandList.SetPipeline(_pipeline);
            // Issue a Draw command for a single instance with 4 indices.
            _commandList.DrawIndexed(
                indexCount: 4,
                instanceCount: 1,
                indexStart: 0,
                vertexOffset: 0,
                instanceStart: 0);

            // End() must be called before commands can be submitted for execution.
            _commandList.End();
            _graphicsDevice.SubmitCommands(_commandList);

            // Once commands have been submitted, the rendered image can be presented to the application window.
            _graphicsDevice.SwapBuffers();
        }
        protected override void OnPause()
        {
            base.OnPause();
            _view.OnPause();
        }
        protected override void OnResume()
        {
            base.OnResume();
            _view.OnResume();
        }
    }
    struct VertexPositionColor
    {
        public const uint SizeInBytes = 24;
        public Vector2 Position;
        public RgbaFloat Color;
        public VertexPositionColor(Vector2 position, RgbaFloat color)
        {
            Position = position;
            Color = color;
        }
    }
}