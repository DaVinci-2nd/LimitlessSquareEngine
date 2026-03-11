using MoonSharp.Interpreter;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics;

namespace LimitlessSquareEngine
{
    [MoonSharpUserData]
    internal class Graphics
    {
        private GL _gl;
        private IWindow _window;
        private uint _quadVAO;
        private uint _quadVBO;
        private bool _quadInitialized = false;
        private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.Ordinal);

        private uint _sceneSolidProgram = 0;
        private long _sceneBatchCounter = 0;

        // 渲染区域
        private bool _sceneViewportUseFixedAspect = false;
        private float _sceneViewportAspectWidth = 16f;
        private float _sceneViewportAspectHeight = 9f;

        // 图形缓存
        private Dictionary<string, uint> _shaderPrograms = new Dictionary<string, uint>();
        // 纹理缓存
        private struct TextureInfo
        {
            public uint Id;
            public bool HasTransparency;
            public int Width;
            public int Height;
        }

        private Dictionary<string, TextureInfo> _textures = new();
        // 激活的着色器序列
        private uint _currentProgram;

        private RenderSpace _activeRenderSpace = RenderSpace.Canvas;

        private Matrix4x4 _activeModelMatrix = Matrix4x4.Identity;
        private Matrix4x4 _activeViewMatrix = Matrix4x4.Identity;
        private Matrix4x4 _activeProjectionMatrix = Matrix4x4.Identity;

        private enum RenderQueueType
        {
            Opaque = 0,
            Transparent = 1
        }

        private enum RenderPass
        {
            Scene = 0,
            Canvas = 1
        }

        private readonly struct MeshData
        {
            public string Id { get; }
            public float[] Vertices { get; }
            public PrimitiveType PrimitiveType { get; }

            public MeshData(string id, float[] vertices, PrimitiveType primitiveType)
            {
                Id = id;
                Vertices = vertices;
                PrimitiveType = primitiveType;
            }
        }

        private readonly struct ViewportRect
        {
            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }

            public float Aspect => Height <= 0 ? 1f : Width / (float)Height;

            public ViewportRect(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }
        }

        private struct RenderCommand
        {
            public float[] Vertices;
            public PrimitiveType PrimitiveType;
            public uint Program;
            public bool UseTexture;
            public uint TextureId;
            public RenderSpace RenderSpace;

            public Matrix4x4 Model;
            public Matrix4x4 View;
            public Matrix4x4 Projection;

            public RenderQueueType QueueType;
            public float SortDepth;
            public long SubmissionIndex;

            public RenderPass Pass;
            public long BatchId;
            public long BatchSubmissionOrder;

            public int ViewportX;
            public int ViewportY;
            public int ViewportWidth;
            public int ViewportHeight;
        }
        private readonly List<RenderCommand> _renderQueue = new();
        private long _submissionCounter = 0;

        // 给以后的相机/场景系统用
        private bool _cameraContextActive = false;
        private int _activeSceneId = -1;

        /// <summary>
        /// 渲染类型枚举
        /// </summary>
        internal enum RenderSpace
        {
            Canvas = 0,
            Camera = 1
        }

        /// <summary>
        /// 透明类型判断
        /// </summary>
        /// <param name="textured"></param>
        /// <param name="textureHasTransparency"></param>
        /// <returns></returns>
        private bool IsCurrentDrawTransparent(bool textured, bool textureHasTransparency = false)
        {
            if (_currentColor.W < 1f)
                return true;

            if (textured && textureHasTransparency)
                return true;

            return false;
        }

        /// <summary>
        /// 深度排序
        /// </summary>
        /// <param name="vertices"></param>
        /// <param name="model"></param>
        /// <param name="view"></param>
        /// <param name="renderSpace"></param>
        /// <returns></returns>
        private float ComputeSortDepth(float[] vertices, Matrix4x4 model, Matrix4x4 view, RenderSpace renderSpace)
        {
            int vertexCount = vertices.Length / 9;
            if (vertexCount == 0) return 0f;

            Vector3 center = Vector3.Zero;
            for (int i = 0; i < vertexCount; i++)
            {
                int idx = i * 9;
                center += new Vector3(vertices[idx], vertices[idx + 1], vertices[idx + 2]);
            }
            center /= vertexCount;

            if (renderSpace == RenderSpace.Canvas)
            {
                Vector4 canvasPos = Vector4.Transform(new Vector4(center, 1f), model);
                return -canvasPos.Z;
            }

            Vector4 world = Vector4.Transform(new Vector4(center, 1f), model);
            Vector4 viewPos = Vector4.Transform(world, view);
            return -viewPos.Z;
        }

        /// <summary>
        /// 透视/正交矩阵
        /// </summary>
        /// <param name="fovRadians"></param>
        /// <param name="aspect"></param>
        /// <param name="near"></param>
        /// <param name="far"></param>
        /// <returns></returns>
        public static Matrix4x4 CreatePerspective(float fovRadians, float aspect, float near, float far)
        {
            return Matrix4x4.CreatePerspectiveFieldOfView(fovRadians, aspect, near, far);
        }

        public static Matrix4x4 CreateOrthographic(float width, float height, float near, float far)
        {
            return Matrix4x4.CreateOrthographic(width, height, near, far);
        }

        /// <summary>
        /// 设置场景全屏窗口
        /// </summary>
        [MoonSharpHidden]
        public void SetSceneViewportFillWindow()
        {
            _sceneViewportUseFixedAspect = false;
        }

        [MoonSharpHidden]
        public void SetSceneViewportFixedAspect(float width, float height)
        {
            if (width <= 0f || height <= 0f)
                throw new ArgumentException("[X] Fixed aspect width/height must be > 0.");

            _sceneViewportUseFixedAspect = true;
            _sceneViewportAspectWidth = width;
            _sceneViewportAspectHeight = height;
        }

        private ViewportRect GetSceneViewportRect()
        {
            int windowWidth = _window.Size.X;
            int windowHeight = _window.Size.Y;

            if (!_sceneViewportUseFixedAspect)
                return new ViewportRect(0, 0, windowWidth, windowHeight);

            float targetAspect = _sceneViewportAspectWidth / _sceneViewportAspectHeight;
            float windowAspect = windowWidth / (float)windowHeight;

            if (windowAspect > targetAspect)
            {
                int viewportHeight = windowHeight;
                int viewportWidth = (int)MathF.Round(viewportHeight * targetAspect);
                int viewportX = (windowWidth - viewportWidth) / 2;
                return new ViewportRect(viewportX, 0, viewportWidth, viewportHeight);
            }
            else
            {
                int viewportWidth = windowWidth;
                int viewportHeight = (int)MathF.Round(viewportWidth / targetAspect);
                int viewportY = (windowHeight - viewportHeight) / 2;
                return new ViewportRect(0, viewportY, viewportWidth, viewportHeight);
            }
        }

        /// <summary>
        /// 加载着色器
        /// </summary>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        private void LoadShaders()
        {
            string shadersPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shaders");
            if (!Directory.Exists(shadersPath))
                throw new DirectoryNotFoundException("[X] Shaders folder not found.");

            // 获取所有着色器
            string[] vertexFiles = Directory.GetFiles(shadersPath, "*.vert", SearchOption.AllDirectories);
            foreach (string vertFile in vertexFiles)
            {
                string directory = Path.GetDirectoryName(vertFile);
                string name = Path.GetFileNameWithoutExtension(vertFile);
                string fragFile = Path.Combine(directory, name + ".frag");
                if (!File.Exists(fragFile))
                {
                    Console.WriteLine($"[!] The frag file corresponding to {vertFile} cannot be found, Skipped.");
                    continue;
                }

                string vertexSource = File.ReadAllText(vertFile);
                string fragmentSource = File.ReadAllText(fragFile);

                uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
                uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

                uint program = _gl.CreateProgram();
                _gl.AttachShader(program, vertexShader);
                _gl.AttachShader(program, fragmentShader);
                _gl.LinkProgram(program);

                _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
                if (success == 0)
                {
                    string infoLog = _gl.GetProgramInfoLog(program);
                    throw new Exception($"[X] Shader '{name}' failed to link: {infoLog}");
                }

                _gl.DetachShader(program, vertexShader);
                _gl.DetachShader(program, fragmentShader);
                _gl.DeleteShader(vertexShader);
                _gl.DeleteShader(fragmentShader);

                string relativePath = vertFile.Substring(shadersPath.Length + 1);
                string key = relativePath.Replace(".vert", "").Replace('\\', '/');
                _shaderPrograms[key] = program;
                Console.WriteLine($"[i] has been successfully loaded {key} shader");
            }

            if (_shaderPrograms.Count == 0)
                throw new Exception("[X] No valid shader found");

            // 设置默认程序
            _currentProgram = _shaderPrograms.Values.First();
            _gl.UseProgram(_currentProgram);
            EnsureSceneSolidShader();
            RegisterBuiltInMeshes();
        }

        /// <summary>
        /// 应用着色器
        /// </summary>
        /// <param name="name"></param>
        public void UseShader(string name)
        {
            if (_shaderPrograms.TryGetValue(name, out uint program))
            {
                if (_currentProgram != program)
                {
                    _currentProgram = program;
                    _gl.UseProgram(program);
                }
            }
            else
            {
                // 未找到着色器时用备用着色器代替
                Console.WriteLine($"[X] Shader '{name}' not found.");
                const string fallbackKey = "__internal_fallback_purple__";
                if (!_shaderPrograms.TryGetValue(fallbackKey, out uint fallbackProgram))
                {
                    fallbackProgram = CreateFallbackShaderProgram();
                    _shaderPrograms[fallbackKey] = fallbackProgram;
                }
                if (_currentProgram != fallbackProgram)
                {
                    _currentProgram = fallbackProgram;
                    _gl.UseProgram(fallbackProgram);
                }
            }
        }

        /// <summary>
        /// 备用着色器
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        private uint CreateFallbackShaderProgram()
        {
            string vertexSource = @"
                #version 330 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec4 aColor;

                uniform int uRenderSpace;
                uniform mat4 uModel;
                uniform mat4 uView;
                uniform mat4 uProjection;

                out vec4 vColor;

                void main()
                {
                    if (uRenderSpace == 0)
                    {
                        gl_Position = vec4(aPos, 1.0);
                    }
                    else
                    {
                        gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
                    }

                    vColor = aColor;
                }";

            string fragmentSource = @"
                #version 330 core
                out vec4 FragColor;
                void main()
                {
                    FragColor = vec4(1.0, 0.0, 1.0, 1.0);
                }";

            uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
            uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vertexShader);
            _gl.AttachShader(program, fragmentShader);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Default shader loading error: {infoLog}");
            }

            _gl.DetachShader(program, vertexShader);
            _gl.DetachShader(program, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            return program;
        }

        //顶点数据缓存
        private List<float> _vertexBuffer = new List<float>();
        private uint _vertexArrayObject;
        private uint _vertexBufferObject;
        private bool _isInitialized = false;

        //当前绘制颜色
        private Vector4 _currentColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        //背景色
        private Vector4 _backgroundColor = new Vector4(0.0f, 0.0f, 0.0f, 1.0f);


        public Graphics(GL gl, IWindow window)
        {
            _gl = gl;
            _window = window;
        }

        /// <summary>
        /// 初始化渲染资源
        /// </summary>
        private void InitQuadRenderer()
        {
            if (_quadInitialized) return;

            _quadVAO = _gl.GenVertexArray();
            _quadVBO = _gl.GenBuffer();

            _gl.BindVertexArray(_quadVAO);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVBO);

            float[] vertices = new float[6 * 9];

            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.DynamicDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 9 * sizeof(float), 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            _gl.BindVertexArray(0);

            _quadInitialized = true;
        }

        /// <summary>
        /// 初始化OpenGL资源
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;
            InitQuadRenderer();
            // 创建VAO和VBO
            _vertexArrayObject = _gl.GenVertexArray();
            _vertexBufferObject = _gl.GenBuffer();

            _gl.BindVertexArray(_vertexArrayObject);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBufferObject);

            // 设置顶点属性指针 (位置: 3 floats, 颜色: 4 floats)
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 9 * sizeof(float), 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);


            // _shaderProgram = CreateShaderProgram();
            LoadShaders();
            _gl.Enable(GLEnum.DepthTest);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);
            _gl.Enable(GLEnum.Blend);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            _isInitialized = true;
        }

        /// <summary>
        /// 编译着色器
        /// </summary>
        private uint CompileShader(ShaderType type, string source)
        {
            uint shader = _gl.CreateShader(type);
            _gl.ShaderSource(shader, source);
            _gl.CompileShader(shader);

            // 检查编译错误
            _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetShaderInfoLog(shader);
                throw new Exception($"[X] Shader compilation failed: {infoLog}");
            }
            return shader;
        }

        /// <summary>
        /// 设置当前绘制颜色 (分量0-1)
        /// </summary>
        public void SetColor(float r, float g, float b, float a = 1.0f)
        {
            _currentColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 设置当前绘制颜色 (整数0-255)
        /// </summary>
        public void SetColorRGB(int r, int g, int b, int a = 255)
        {
            _currentColor = new Vector4(r / 255.0f, g / 255.0f, b / 255.0f, a / 255.0f);
        }

        /// <summary>
        /// 设置背景色
        /// </summary>
        public void SetBackgroundColor(float r, float g, float b, float a = 1.0f)
        {
            _backgroundColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 执行清屏
        /// </summary>
        [MoonSharpHidden]
        public void ClearBackground()
        {
            _gl.ClearColor(_backgroundColor.X, _backgroundColor.Y, _backgroundColor.Z, _backgroundColor.W);
            _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }


        /// <summary>
        /// 绘制单个点
        /// </summary>
        public void DrawPoint(float x, float y, float z = 0)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x, y, z, 0f, 0f);
            Flush(PrimitiveType.Points);
        }

        /// <summary>
        /// 批量绘制多个点
        /// </summary>
        public void DrawPoints(Table points)
        {
            EnsureLuaCanvasMode();
            // 清空缓冲
            _vertexBuffer.Clear();

            // 将Lua表转换为顶点数据
            for (int i = 1; i <= points.Length; i += 3)
            {
                float x = (float)points.Get(i).Number;
                float y = (float)points.Get(i + 1).Number;
                float z = (float)points.Get(i + 2).Number;

                AddVertex(x, y, z);
            }

            // 批量绘制
            Flush(PrimitiveType.Points);
        }

        /// <summary>
        /// 绘制线条（两点一线）
        /// </summary>
        public void DrawLine(float x1, float y1, float z1, float x2, float y2, float z2)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            Flush(PrimitiveType.Lines);
        }

        /// <summary>
        /// 绘制连续线条（折线）
        /// </summary>
        public void DrawLineStrip(Table points)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();

            for (int i = 1; i <= points.Length; i += 3)
            {
                float x = (float)points.Get(i).Number;
                float y = (float)points.Get(i + 1).Number;
                float z = (float)points.Get(i + 2).Number;

                AddVertex(x, y, z);
            }

            Flush(PrimitiveType.LineStrip);
        }

        /// <summary>
        /// 绘制三角形
        /// </summary>
        public void DrawTriangle(float x1, float y1, float z1,
                                 float x2, float y2, float z2,
                                 float x3, float y3, float z3)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);
            Flush(PrimitiveType.Triangles);
        }

        /// <summary>
        /// 绘制四边形
        /// </summary>
        public void DrawQuad(
            float x1, float y1, float z1,
            float x2, float y2, float z2,
            float x3, float y3, float z3,
            float x4, float y4, float z4)
        {
            EnsureLuaCanvasMode();
            _vertexBuffer.Clear();

            // triangle 1
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);

            // triangle 2
            AddVertex(x3, y3, z3);
            AddVertex(x4, y4, z4);
            AddVertex(x1, y1, z1);

            Flush(PrimitiveType.Triangles);
        }

        /// <summary>
        /// 绘制矩形（2D平面）
        /// </summary>
        public void DrawRect(float x, float y, float width, float height)
        {
            EnsureLuaCanvasMode();
            DrawQuad(x, y, 0,
                    x + width, y, 0,
                    x + width, y + height, 0,
                    x, y + height, 0);
        }
        /// <summary>
        /// 绘制纹理面
        /// </summary>
        /// <param name="x1"></param>
        /// <param name="y1"></param>
        /// <param name="z1"></param>
        /// <param name="x2"></param>
        /// <param name="y2"></param>
        /// <param name="z2"></param>
        /// <param name="x3"></param>
        /// <param name="y3"></param>
        /// <param name="z3"></param>
        /// <param name="x4"></param>
        /// <param name="y4"></param>
        /// <param name="z4"></param>
        /// <param name="texturePath"></param>
        public void DrawTextured(
            float x1, float y1, float z1,
            float x2, float y2, float z2,
            float x3, float y3, float z3,
            float x4, float y4, float z4,
            string texturePath)
        {
            EnsureLuaCanvasMode();

            int texLoc = _gl.GetUniformLocation(_currentProgram, "uTexture");
            if (texLoc == -1)
            {
                Console.WriteLine("[X] Current shader does not support texture");
                return;
            }

            string fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", texturePath);
            TextureInfo tex = LoadTexture(fullPath);
            if (tex.Id == 0)
            {
                Console.WriteLine($"[X] Texture not found: {fullPath}");
                return;
            }

            float[] vertices =
            {
                x1,y1,z1, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,0,
                x2,y2,z2, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,0,
                x3,y3,z3, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,1,

                x3,y3,z3, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1,1,
                x4,y4,z4, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,1,
                x1,y1,z1, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0,0
            };

            bool transparent = IsCurrentDrawTransparent(true, tex.HasTransparency);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = PrimitiveType.Triangles,
                Program = _currentProgram,
                UseTexture = true,
                TextureId = tex.Id,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y
            };

            _renderQueue.Add(cmd);
        }

        /// <summary>
        /// 绘制带纹理的四边形
        /// </summary>
        public void DrawTexturedQuad(float x1, float y1, float x2, float y2, string texturePath)
        {
            EnsureLuaCanvasMode();

            int texLoc = _gl.GetUniformLocation(_currentProgram, "uTexture");
            if (texLoc == -1)
            {
                Console.WriteLine("[X] Current shader does not support texture");
                return;
            }

            string fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", texturePath);
            TextureInfo tex = LoadTexture(fullPath);
            if (tex.Id == 0)
            {
                Console.WriteLine($"[X] The texture file does not exist: {fullPath}");
                return;
            }

            float[] vertices =
            {
                x1, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 0,
                x2, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 0,
                x2, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 1,

                x2, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 1, 1,
                x1, y2, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 1,
                x1, y1, 0, _currentColor.X,_currentColor.Y,_currentColor.Z,_currentColor.W, 0, 0
            };

            bool transparent = IsCurrentDrawTransparent(true, tex.HasTransparency);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = PrimitiveType.Triangles,
                Program = _currentProgram,
                UseTexture = true,
                TextureId = tex.Id,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y
            };

            _renderQueue.Add(cmd);
        }




        /// <summary>
        /// 从文件加载纹理
        /// </summary>
        private TextureInfo LoadTexture(string path)
        {
            if (_textures.TryGetValue(path, out TextureInfo existingTex))
                return existingTex;

            if (!File.Exists(path))
            {
                Console.WriteLine($"[X] The texture file does not exist: {path}");
                return default;
            }

            try
            {
                using (Image<Rgba32> image = Image.Load<Rgba32>(path))
                {
                    image.Mutate(x => x.Flip(FlipMode.Vertical));

                    uint texture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, texture);

                    int pixelCount = image.Width * image.Height;
                    Rgba32[] pixels = new Rgba32[pixelCount];
                    image.CopyPixelDataTo(pixels);

                    byte[] pixelBytes = new byte[pixelCount * 4];
                    bool hasTransparency = false;

                    for (int i = 0; i < pixelCount; i++)
                    {
                        pixelBytes[i * 4] = pixels[i].R;
                        pixelBytes[i * 4 + 1] = pixels[i].G;
                        pixelBytes[i * 4 + 2] = pixels[i].B;
                        pixelBytes[i * 4 + 3] = pixels[i].A;

                        if (pixels[i].A < 255)
                            hasTransparency = true;
                    }

                    _gl.TexImage2D(
                        TextureTarget.Texture2D,
                        0,
                        InternalFormat.Rgba,
                        (uint)image.Width,
                        (uint)image.Height,
                        0,
                        PixelFormat.Rgba,
                        PixelType.UnsignedByte,
                        (ReadOnlySpan<byte>)pixelBytes);

                    _gl.GenerateMipmap(TextureTarget.Texture2D);

                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                    TextureInfo info = new TextureInfo
                    {
                        Id = texture,
                        HasTransparency = hasTransparency,
                        Width = image.Width,
                        Height = image.Height
                    };

                    _textures[path] = info;
                    return info;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] Failed to load texture {path}: {ex.Message}");
                return default;
            }
        }

        // ==================== UI 绘制方法 ====================

        /// <summary>
        /// 将屏幕像素坐标转换为NDC坐标
        /// </summary>
        private (float ndcX, float ndcY) PixelToNDC(float pixelX, float pixelY)
        {
            float halfWidth = _window.Size.X / 2.0f;
            float halfHeight = _window.Size.Y / 2.0f;
            float ndcX = (pixelX - halfWidth) / halfWidth;
            float ndcY = (halfHeight - pixelY) / halfHeight;
            return (ndcX, ndcY);
        }

        /// <summary>
        /// 绘制一个UI元素树
        /// </summary>
        public void DrawUI(UIElement root)
        {
            UseCanvasSpace();
            DrawUIElement(root);
        }

        /// <summary>
        /// 递归绘制UI元素
        /// </summary>
        private void DrawUIElement(UIElement element)
        {
            if (!element.Visible)
                return;

            Vector4 oldColor = _currentColor;

            if (element.BackgroundColor.W > 0)
            {
                SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                (float x1, float y1) = PixelToNDC(element.X, element.Y);
                (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
            }

            switch (element.Type)
            {
                case UIElementType.Label:
                case UIElementType.Button:
                    if (!string.IsNullOrEmpty(element.Text))
                    {
                        SetColor(element.TextColor.X, element.TextColor.Y, element.TextColor.Z, element.TextColor.W);
                        (float tx1, float ty1) = PixelToNDC(element.X + 5, element.Y + 5);
                        (float tx2, float ty2) = PixelToNDC(element.X + element.Width - 5, element.Y + element.Height - 5);
                        DrawQuad(tx1, ty1, 0, tx2, ty1, 0, tx2, ty2, 0, tx1, ty2, 0);
                    }
                    break;

                case UIElementType.Image:
                    if (!string.IsNullOrEmpty(element.ImageSource))
                    {
                        string fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", element.ImageSource);
                        TextureInfo tex = LoadTexture(fullPath);
                        uint texId = tex.Id;
                        if (texId != 0)
                        {
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawTexturedQuad(x1, y1, x2, y2, element.ImageSource);
                        }
                        else
                        {
                            // 纹理加载失败，用背景色填充
                            SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
                        }
                    }
                    else
                    {
                        // 没有图片源，用背景色填充
                        SetColor(element.BackgroundColor.X, element.BackgroundColor.Y, element.BackgroundColor.Z, element.BackgroundColor.W);
                        (float x1, float y1) = PixelToNDC(element.X, element.Y);
                        (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                        DrawQuad(x1, y1, 0, x2, y1, 0, x2, y2, 0, x1, y2, 0);
                    }
                    break;
            }

            SetColor(oldColor.X, oldColor.Y, oldColor.Z, oldColor.W);

            foreach (var child in element.Children)
            {
                DrawUIElement(child);
            }
        }

        // 相机系统调用接口
        public void BeginCameraRender(Matrix4x4 view, Matrix4x4 projection, int sceneId = -1)
        {
            _cameraContextActive = true;
            _activeRenderSpace = RenderSpace.Camera;
            _activeViewMatrix = view;
            _activeProjectionMatrix = projection;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeSceneId = sceneId;
        }

        public void SetModelMatrix(Matrix4x4 model)
        {
            _activeModelMatrix = model;
        }

        public void EndCameraRender()
        {
            _cameraContextActive = false;
            _activeRenderSpace = RenderSpace.Canvas;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = Matrix4x4.Identity;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeSceneId = -1;
        }

        [MoonSharpHidden]
        public void QueueLoadedSceneRender()
        {
            if (!_isInitialized)
                Initialize();

            EnsureSceneSolidShader();
            RegisterBuiltInMeshes();

            foreach (SceneData scene in Scene.GetLoadedScenes())
            {
                IReadOnlyList<SceneCameraQueueItem> cameraQueue = Scene.GetCameraQueue(scene.SceneId);
                if (cameraQueue.Count == 0)
                    continue;

                Dictionary<string, SceneWorldState> worldStates = Scene.BuildWorldStates(scene);

                foreach (SceneCameraQueueItem cameraItem in cameraQueue.OrderBy(c => c.SubmissionOrder))
                {
                    QueueSceneCamera(scene, worldStates, cameraItem);
                }
            }
        }

        private void QueueSceneCamera(
            SceneData scene,
            Dictionary<string, SceneWorldState> worldStates,
            SceneCameraQueueItem cameraItem)
        {
            // 模式1本轮明确忽略
            if (cameraItem.Settings.RenderMode != 0)
                return;

            if (!worldStates.TryGetValue(cameraItem.ObjectId, out SceneWorldState cameraWorld))
                return;

            ViewportRect viewport = GetSceneViewportRect();
            Matrix4x4 view = CreateSceneViewMatrix(cameraWorld);
            Matrix4x4 projection = CreateSceneProjection(cameraItem.Settings, viewport.Aspect);

            long batchId = ++_sceneBatchCounter;

            foreach (SceneObject obj in scene.Objects)
            {
                if (!obj.Active || !obj.Visible)
                    continue;

                if (obj.Id == cameraItem.ObjectId)
                    continue;

                if (string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                    continue;

                if (string.IsNullOrWhiteSpace(obj.Mesh))
                    continue;

                if (!_meshes.TryGetValue(obj.Mesh, out MeshData mesh))
                {
                    Console.WriteLine($"[!] Mesh '{obj.Mesh}' not found for object '{obj.Id}'.");
                    continue;
                }

                if (!worldStates.TryGetValue(obj.Id, out SceneWorldState objectWorld))
                    continue;

                // 双精度绝对坐标 -> 相机相对双精度 -> 单精度提交GPU
                Double3 relativePosition = objectWorld.Position - cameraWorld.Position;

                Matrix4x4 model =
                    Matrix4x4.CreateScale((float)objectWorld.Scale.X, (float)objectWorld.Scale.Y, (float)objectWorld.Scale.Z) *
                    Matrix4x4.CreateFromQuaternion(objectWorld.Rotation.ToSingle()) *
                    Matrix4x4.CreateTranslation((float)relativePosition.X, (float)relativePosition.Y, (float)relativePosition.Z);

                var cmd = new RenderCommand
                {
                    Vertices = mesh.Vertices,
                    PrimitiveType = mesh.PrimitiveType,
                    Program = _sceneSolidProgram,
                    UseTexture = false,
                    TextureId = 0,
                    RenderSpace = RenderSpace.Camera,
                    Model = model,
                    View = view,
                    Projection = projection,

                    QueueType = RenderQueueType.Opaque,
                    SortDepth = ComputeSortDepth(mesh.Vertices, model, view, RenderSpace.Camera),
                    SubmissionIndex = _submissionCounter++,

                    Pass = RenderPass.Scene,
                    BatchId = batchId,
                    BatchSubmissionOrder = cameraItem.SubmissionOrder,
                    ViewportX = viewport.X,
                    ViewportY = viewport.Y,
                    ViewportWidth = viewport.Width,
                    ViewportHeight = viewport.Height
                };

                _renderQueue.Add(cmd);
            }
        }

        private Matrix4x4 CreateSceneViewMatrix(SceneWorldState cameraWorld)
        {
            Quaternion cameraRotation = cameraWorld.Rotation.ToSingle();
            Quaternion inverse = Quaternion.Inverse(cameraRotation);
            return Matrix4x4.CreateFromQuaternion(inverse);
        }

        private Matrix4x4 CreateSceneProjection(CameraRenderSettings settings, float aspect)
        {
            float near = (float)settings.NearClip;
            float far = (float)settings.FarClip;

            if (settings.ProjectionType == 1)
            {
                // 正交
                float height = (float)settings.FovOrSize;
                float width = height * aspect;
                return CreateOrthographic(width, height, near, far);
            }
            else
            {
                // 透视
                float fovRadians = (float)(settings.FovOrSize * Math.PI / 180.0);
                return CreatePerspective(fovRadians, aspect, near, far);
            }
        }

        // 上传辅助函数
        private void ApplyRenderUniforms(bool useTexture)
        {
            int renderSpaceLoc = _gl.GetUniformLocation(_currentProgram, "uRenderSpace");
            if (renderSpaceLoc != -1)
                _gl.Uniform1(renderSpaceLoc, (int)_activeRenderSpace);

            int useTexLoc = _gl.GetUniformLocation(_currentProgram, "uUseTexture");
            if (useTexLoc != -1)
                _gl.Uniform1(useTexLoc, useTexture ? 1 : 0);

            int colorLoc = _gl.GetUniformLocation(_currentProgram, "uColor");
            if (colorLoc != -1)
                _gl.Uniform4(colorLoc, 1f, 1f, 1f, 1f);

            int modelLoc = _gl.GetUniformLocation(_currentProgram, "uModel");
            if (modelLoc != -1)
                SetMatrixUniform(modelLoc, _activeModelMatrix);

            int viewLoc = _gl.GetUniformLocation(_currentProgram, "uView");
            if (viewLoc != -1)
                SetMatrixUniform(viewLoc, _activeViewMatrix);

            int projLoc = _gl.GetUniformLocation(_currentProgram, "uProjection");
            if (projLoc != -1)
                SetMatrixUniform(projLoc, _activeProjectionMatrix);
        }
        // 矩阵上传函数
        private void SetMatrixUniform(int location, Matrix4x4 matrix)
        {
            float[] values =
            {
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44
    };

            _gl.UniformMatrix4(location, 1, false, values);
        }

        /// <summary>
        /// 在Canvas层
        /// </summary>
        private void UseCanvasSpace()
        {
            _activeRenderSpace = RenderSpace.Canvas;
            _activeModelMatrix = Matrix4x4.Identity;
            _activeViewMatrix = Matrix4x4.Identity;
            _activeProjectionMatrix = Matrix4x4.Identity;
        }

        private void EnsureLuaCanvasMode()
        {
            if (!_cameraContextActive)
                UseCanvasSpace();
        }

        [MoonSharpHidden]
        public void ExecuteRenderQueue()
        {
            if (!_isInitialized)
                Initialize();

            if (_renderQueue.Count == 0)
                return;

            InitQuadRenderer();

            List<RenderCommand> sceneCommands = _renderQueue
                .Where(c => c.Pass == RenderPass.Scene)
                .ToList();

            List<RenderCommand> canvasCommands = _renderQueue
                .Where(c => c.Pass == RenderPass.Canvas)
                .ToList();

            ExecuteScenePass(sceneCommands);
            ExecuteCanvasPass(canvasCommands);

            _renderQueue.Clear();
        }

        private void ExecuteScenePass(List<RenderCommand> sceneCommands)
        {
            if (sceneCommands.Count == 0)
                return;

            var batches = sceneCommands
                .GroupBy(c => c.BatchId)
                .OrderBy(g => g.First().BatchSubmissionOrder)
                .ToList();

            foreach (var batch in batches)
            {
                RenderCommand first = batch.First();

                int vpX = first.ViewportX;
                int vpY = first.ViewportY;
                uint vpW = (uint)Math.Max(1, first.ViewportWidth);
                uint vpH = (uint)Math.Max(1, first.ViewportHeight);

                _gl.Viewport(vpX, vpY, vpW, vpH);

                _gl.Enable(GLEnum.ScissorTest);
                _gl.Scissor(vpX, vpY, vpW, vpH);
                _gl.ClearColor(_backgroundColor.X, _backgroundColor.Y, _backgroundColor.Z, _backgroundColor.W);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                _gl.Disable(GLEnum.ScissorTest);

                ExecuteSortedCommands(batch.ToList());
            }

            // 场景结束后恢复整窗viewport并清一次深度
            _gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
            _gl.Clear(ClearBufferMask.DepthBufferBit);
        }

        private void ExecuteCanvasPass(List<RenderCommand> canvasCommands)
        {
            if (canvasCommands.Count == 0)
                return;

            _gl.Viewport(0, 0, (uint)_window.Size.X, (uint)_window.Size.Y);
            ExecuteSortedCommands(canvasCommands);
        }

        private void ExecuteSortedCommands(List<RenderCommand> commands)
        {
            var opaque = commands
                .Where(c => c.QueueType == RenderQueueType.Opaque)
                .OrderBy(c => c.SubmissionIndex)
                .ToList();

            var transparent = commands
                .Where(c => c.QueueType == RenderQueueType.Transparent)
                .OrderByDescending(c => c.SortDepth)
                .ThenBy(c => c.SubmissionIndex)
                .ToList();

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Less);
            _gl.DepthMask(true);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            foreach (var cmd in opaque)
                ExecuteCommand(cmd);

            _gl.Enable(GLEnum.DepthTest);
            _gl.DepthFunc(GLEnum.Lequal);
            _gl.DepthMask(false);
            _gl.Enable(GLEnum.Blend);
            _gl.BlendFunc(GLEnum.SrcAlpha, GLEnum.OneMinusSrcAlpha);

            foreach (var cmd in transparent)
                ExecuteCommand(cmd);

            _gl.DepthMask(true);
            _gl.DepthFunc(GLEnum.Less);
        }

        private void ExecuteCommand(RenderCommand cmd)
        {
            _currentProgram = cmd.Program;
            _gl.UseProgram(cmd.Program);

            _activeRenderSpace = cmd.RenderSpace;
            _activeModelMatrix = cmd.Model;
            _activeViewMatrix = cmd.View;
            _activeProjectionMatrix = cmd.Projection;

            _gl.BindVertexArray(_quadVAO);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _quadVBO);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)cmd.Vertices, BufferUsageARB.DynamicDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 9 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 9 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, 9 * sizeof(float), 7 * sizeof(float));
            _gl.EnableVertexAttribArray(2);

            ApplyRenderUniforms(cmd.UseTexture);

            if (cmd.UseTexture)
            {
                int texLoc = _gl.GetUniformLocation(cmd.Program, "uTexture");
                if (texLoc != -1)
                {
                    _gl.ActiveTexture(TextureUnit.Texture0);
                    _gl.BindTexture(TextureTarget.Texture2D, cmd.TextureId);
                    _gl.Uniform1(texLoc, 0);
                }
            }
            else
            {
                _gl.BindTexture(TextureTarget.Texture2D, 0);
            }

            _gl.DrawArrays(cmd.PrimitiveType, 0, (uint)(cmd.Vertices.Length / 9));

            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
        }

        /// <summary>
        /// 网格注册器
        /// </summary>
        /// <param name="id"></param>
        /// <param name="vertices"></param>
        /// <param name="primitiveType"></param>
        /// <exception cref="ArgumentException"></exception>
        [MoonSharpHidden]
        public void RegisterMesh(string id, float[] vertices, PrimitiveType primitiveType = PrimitiveType.Triangles)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("[X] Mesh id cannot be null or empty.", nameof(id));

            if (vertices == null || vertices.Length == 0 || vertices.Length % 9 != 0)
                throw new ArgumentException("[X] Mesh vertices must be non-empty and aligned to 9 floats per vertex.", nameof(vertices));

            _meshes[id] = new MeshData(id, vertices, primitiveType);
        }

        private void EnsureSceneSolidShader()
        {
            if (_sceneSolidProgram != 0)
                return;

            const string vertexSource = @"
                #version 330 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec4 aColor;

                uniform mat4 uModel;
                uniform mat4 uView;
                uniform mat4 uProjection;

                out vec4 vColor;

                void main()
                {
                    gl_Position = uProjection * uView * uModel * vec4(aPos, 1.0);
                    vColor = aColor;
                }";

            const string fragmentSource = @"
                #version 330 core
                in vec4 vColor;
                out vec4 FragColor;

                void main()
                {
                    FragColor = vColor;
                }";

            uint vertexShader = CompileShader(ShaderType.VertexShader, vertexSource);
            uint fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentSource);

            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vertexShader);
            _gl.AttachShader(program, fragmentShader);
            _gl.LinkProgram(program);

            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"[X] Scene solid shader link failed: {infoLog}");
            }

            _gl.DetachShader(program, vertexShader);
            _gl.DetachShader(program, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            _sceneSolidProgram = program;
        }

        private void RegisterBuiltInMeshes()
        {
            if (_meshes.ContainsKey("builtin/cube_1x1x1"))
                return;

            RegisterMesh("builtin/cube_1x1x1", CreateUnitCubeVertices(), PrimitiveType.Triangles);
        }

        private float[] CreateUnitCubeVertices()
        {
            var data = new List<float>(36 * 9);

            void AddVertex(float x, float y, float z)
            {
                data.Add(x);
                data.Add(y);
                data.Add(z);

                data.Add(1f);
                data.Add(1f);
                data.Add(1f);
                data.Add(1f);

                data.Add(0f);
                data.Add(0f);
            }

            void AddTri(
                float ax, float ay, float az,
                float bx, float by, float bz,
                float cx, float cy, float cz)
            {
                AddVertex(ax, ay, az);
                AddVertex(bx, by, bz);
                AddVertex(cx, cy, cz);
            }

            float n = 0.5f;

            // +Z
            AddTri(-n, -n, n, n, -n, n, n, n, n);
            AddTri(n, n, n, -n, n, n, -n, -n, n);

            // -Z
            AddTri(n, -n, -n, -n, -n, -n, -n, n, -n);
            AddTri(-n, n, -n, n, n, -n, n, -n, -n);

            // -X
            AddTri(-n, -n, -n, -n, -n, n, -n, n, n);
            AddTri(-n, n, n, -n, n, -n, -n, -n, -n);

            // +X
            AddTri(n, -n, n, n, -n, -n, n, n, -n);
            AddTri(n, n, -n, n, n, n, n, -n, n);

            // +Y
            AddTri(-n, n, n, n, n, n, n, n, -n);
            AddTri(n, n, -n, -n, n, -n, -n, n, n);

            // -Y
            AddTri(-n, -n, -n, n, -n, -n, n, -n, n);
            AddTri(n, -n, n, -n, -n, n, -n, -n, -n);

            return data.ToArray();
        }

        /// <summary>
        /// 添加带UV顶点到缓冲区
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="z"></param>
        /// <param name="u"></param>
        /// <param name="v"></param>
        private void AddVertex(float x, float y, float z, float u = 0f, float v = 0f)
        {
            _vertexBuffer.Add(x); _vertexBuffer.Add(y); _vertexBuffer.Add(z);
            _vertexBuffer.Add(_currentColor.X); _vertexBuffer.Add(_currentColor.Y);
            _vertexBuffer.Add(_currentColor.Z); _vertexBuffer.Add(_currentColor.W);
            _vertexBuffer.Add(u); _vertexBuffer.Add(v);
        }

        /// <summary>
        /// 刷新缓冲区到GPU并绘制
        /// </summary>
        /// <param name="primitiveType"></param>
        private void Flush(PrimitiveType primitiveType)
        {
            if (_vertexBuffer.Count == 0) return;
            if (!_isInitialized) Initialize();

            var vertices = _vertexBuffer.ToArray();

            bool transparent = IsCurrentDrawTransparent(false);

            var cmd = new RenderCommand
            {
                Vertices = vertices,
                PrimitiveType = primitiveType,
                Program = _currentProgram,
                UseTexture = false,
                TextureId = 0,
                RenderSpace = _activeRenderSpace,
                Model = _activeModelMatrix,
                View = _activeViewMatrix,
                Projection = _activeProjectionMatrix,
                QueueType = transparent ? RenderQueueType.Transparent : RenderQueueType.Opaque,
                SortDepth = ComputeSortDepth(vertices, _activeModelMatrix, _activeViewMatrix, _activeRenderSpace),
                SubmissionIndex = _submissionCounter++,
                Pass = RenderPass.Canvas,
                BatchId = -1,
                BatchSubmissionOrder = -1,
                ViewportX = 0,
                ViewportY = 0,
                ViewportWidth = _window.Size.X,
                ViewportHeight = _window.Size.Y
            };

            _renderQueue.Add(cmd);
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        [MoonSharpHidden]
        public void Cleanup()
        {
            if (_isInitialized)
            {
                _gl.DeleteVertexArray(_vertexArrayObject);
                _gl.DeleteBuffer(_vertexBufferObject);
                _isInitialized = false;
            }
        }
    }
}
