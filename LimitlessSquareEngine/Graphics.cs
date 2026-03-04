using MoonSharp.Interpreter;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Numerics;
using System.Runtime.InteropServices;

namespace LimitlessSquareEngine
{
    [MoonSharpUserData]
    internal class Graphics
    {
        private GL _gl;
        private IWindow _window;

        // 图形缓存
        private Dictionary<string, uint> _shaderPrograms = new Dictionary<string, uint>();
        // 纹理缓存
        private Dictionary<string, uint> _textures = new Dictionary<string, uint>();
        // 激活的着色器序列
        private uint _currentProgram;

        /// <summary>
        /// 加载着色器
        /// </summary>
        /// <exception cref="DirectoryNotFoundException"></exception>
        /// <exception cref="Exception"></exception>
        private void LoadShaders()
        {
            string shadersPath = Path.Combine(AppContext.BaseDirectory, "Shaders");
            if (!Directory.Exists(shadersPath))
                throw new DirectoryNotFoundException("Shaders folder not found.");

            // 获取所有着色器
            string[] vertexFiles = Directory.GetFiles(shadersPath, "*.vert", SearchOption.AllDirectories);
            foreach (string vertFile in vertexFiles)
            {
                string directory = Path.GetDirectoryName(vertFile);
                string name = Path.GetFileNameWithoutExtension(vertFile);
                string fragFile = Path.Combine(directory, name + ".frag");
                if (!File.Exists(fragFile))
                {
                    Console.WriteLine($"找不到与 {vertFile} 对应的frag文件，已跳过。");
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
                    throw new Exception($"着色器 '{name}' 链接失败：{infoLog}");
                }

                _gl.DetachShader(program, vertexShader);
                _gl.DetachShader(program, fragmentShader);
                _gl.DeleteShader(vertexShader);
                _gl.DeleteShader(fragmentShader);

                string relativePath = vertFile.Substring(shadersPath.Length + 1);
                string key = relativePath.Replace(".vert", "").Replace('\\', '/');
                _shaderPrograms[key] = program;
                Console.WriteLine($"已加载着色器 {key}");
            }

            if (_shaderPrograms.Count == 0)
                throw new Exception("未找到任何有效的着色器");

            // 设置默认程序
            _shaderProgram = _shaderPrograms.Values.First();
            _currentProgram = _shaderProgram;
            _gl.UseProgram(_currentProgram);
        }

        /// <summary>
        /// 应用着色器
        /// </summary>
        /// <param name="name"></param>
        /// <exception cref="ScriptRuntimeException"></exception>
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
                Console.WriteLine($"Shader '{name}' not found.");
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
                out vec4 vColor;
                void main()
                {
                    gl_Position = vec4(aPos, 1.0);
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
                throw new Exception($"后备紫色着色器链接失败：{infoLog}");
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
        private uint _shaderProgram;
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
        /// 初始化OpenGL资源
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            // 创建VAO和VBO
            _vertexArrayObject = _gl.GenVertexArray();
            _vertexBufferObject = _gl.GenBuffer();

            _gl.BindVertexArray(_vertexArrayObject);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBufferObject);

            // 设置顶点属性指针 (位置: 3 floats, 颜色: 4 floats)
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);

            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            // _shaderProgram = CreateShaderProgram();
            LoadShaders();

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);

            _isInitialized = true;
        }

        /// <summary>
        /// 纹理着色器
        /// </summary>
        private uint CreateTexturedShaderProgram()
        {
            string vertexSource = @"
                #version 330 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec2 aTexCoord;
                out vec2 vTexCoord;
                void main()
                {
                    gl_Position = vec4(aPos, 1.0);
                    vTexCoord = aTexCoord;
                }";

            string fragmentSource = @"
                #version 330 core
                uniform sampler2D uTexture;
                in vec2 vTexCoord;
                out vec4 FragColor;
                void main()
                {
                    FragColor = texture(uTexture, vTexCoord);
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
                throw new Exception($"纹理着色器链接失败：{infoLog}");
            }

            _gl.DetachShader(program, vertexShader);
            _gl.DetachShader(program, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            return program;
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
                throw new Exception($"Shader compilation failed: {infoLog}");
            }
            return shader;
        }

        /*
        /// <summary>
        /// 创建着色器程序
        /// </summary>
        private uint CreateShaderProgram()
        {
            // 扫描着色器文件夹
            string shadersPath = Path.Combine(AppContext.BaseDirectory, "Shaders");
            if (!Directory.Exists(shadersPath))
                throw new DirectoryNotFoundException("Shaders folder not found.");
            // 顶点着色器源码
            string vertexSource = @"
                #version 330 core
                layout(location = 0) in vec3 aPos;
                layout(location = 1) in vec4 aColor;
                out vec4 vColor;
                void main()
                {
                    gl_Position = vec4(aPos, 1.0);
                    vColor = aColor;
                }";

            // 片段着色器源码
            string fragmentSource = @"
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

            // 检查链接错误
            _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int success);
            if (success == 0)
            {
                string infoLog = _gl.GetProgramInfoLog(program);
                throw new Exception($"Shader program linking failed: {infoLog}");
            }

            // 链接成功后可以删除着色器对象
            _gl.DetachShader(program, vertexShader);
            _gl.DetachShader(program, fragmentShader);
            _gl.DeleteShader(vertexShader);
            _gl.DeleteShader(fragmentShader);

            return program;
        }
        */

        /// <summary>
        /// 设置当前绘制颜色 (每个分量0-1)
        /// </summary>
        public void SetColor(float r, float g, float b, float a = 1.0f)
        {
            _currentColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 设置当前绘制颜色 (使用整数0-255)
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
            _gl.Clear(ClearBufferMask.ColorBufferBit);
        }


        /// <summary>
        /// 绘制单个点
        /// </summary>
        public void DrawPoint(float x, float y, float z = 0)
        {
            // 创建临时顶点数据
            float[] vertices = new float[]
            {
        x, y, z, _currentColor.X, _currentColor.Y, _currentColor.Z, _currentColor.W
            };

            // 创建临时缓冲并绘制
            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();

            _gl.BindVertexArray(vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.StaticDraw);

            // 设置顶点属性
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            _gl.DrawArrays(PrimitiveType.Points, 0, 1);

            // 清理
            _gl.DeleteVertexArray(vao);
            _gl.DeleteBuffer(vbo);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        /// <summary>
        /// 批量绘制多个点
        /// </summary>
        public void DrawPoints(Table points)
        {
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
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);
            Flush(PrimitiveType.Triangles);
        }

        /// <summary>
        /// 绘制四边形
        /// </summary>
        public void DrawQuad(float x1, float y1, float z1,
                            float x2, float y2, float z2,
                            float x3, float y3, float z3,
                            float x4, float y4, float z4)
        {
            _vertexBuffer.Clear();
            AddVertex(x1, y1, z1);
            AddVertex(x2, y2, z2);
            AddVertex(x3, y3, z3);
            AddVertex(x4, y4, z4);
            Flush(PrimitiveType.Quads);
        }

        /// <summary>
        /// 绘制矩形（2D平面）
        /// </summary>
        public void DrawRect(float x, float y, float width, float height)
        {
            DrawQuad(x, y, 0,
                    x + width, y, 0,
                    x + width, y + height, 0,
                    x, y + height, 0);
        }


        /// <summary>
        /// 从文件加载纹理（使用SixLabors.ImageSharp）
        /// </summary>
        private uint LoadTexture(string path)
        {
            if (_textures.TryGetValue(path, out uint existingTex))
                return existingTex;

            if (!File.Exists(path))
            {
                Console.WriteLine($"纹理文件不存在: {path}");
                return 0;
            }

            try
            {
                using (Image<Rgba32> image = Image.Load<Rgba32>(path))
                {
                    // 翻转图像，因为OpenGL原点在左下角
                    image.Mutate(x => x.Flip(FlipMode.Vertical));

                    uint texture = _gl.GenTexture();
                    _gl.BindTexture(TextureTarget.Texture2D, texture);

                    // 分配缓冲区并复制像素数据
                    int pixelCount = image.Width * image.Height;
                    Rgba32[] pixels = new Rgba32[pixelCount];
                    image.CopyPixelDataTo(pixels);

                    // 转换为字节数组
                    byte[] pixelBytes = new byte[pixelCount * 4];
                    for (int i = 0; i < pixelCount; i++)
                    {
                        pixelBytes[i * 4] = pixels[i].R;
                        pixelBytes[i * 4 + 1] = pixels[i].G;
                        pixelBytes[i * 4 + 2] = pixels[i].B;
                        pixelBytes[i * 4 + 3] = pixels[i].A;
                    }

                    _gl.TexImage2D(TextureTarget.Texture2D,
                            0,
                            InternalFormat.Rgba,
                            (uint)image.Width,
                            (uint)image.Height,
                            0,
                            PixelFormat.Rgba,
                            PixelType.UnsignedByte,
                            (ReadOnlySpan<byte>)pixelBytes);

                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                    _textures[path] = texture;
                    return texture;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载纹理失败 {path}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// 绘制带纹理的四边形（使用纹理着色器）
        /// </summary>
        private void DrawTexturedQuad(float x1, float y1, float x2, float y2, uint textureId)
        {
            if (textureId == 0) return;

            float[] vertices = new float[]
            {
                x1, y1, 0,   0, 0,
                x2, y1, 0,   1, 0,
                x2, y2, 0,   1, 1,
                x1, y2, 0,   0, 1
            };
            uint[] indices = new uint[] { 0, 1, 2, 2, 3, 0 };

            uint vao = _gl.GenVertexArray();
            uint vbo = _gl.GenBuffer();
            uint ebo = _gl.GenBuffer();

            _gl.BindVertexArray(vao);

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.StaticDraw);

            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (ReadOnlySpan<uint>)indices, BufferUsageARB.StaticDraw);

            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            uint prevProgram = _currentProgram;
            if (_shaderPrograms.TryGetValue("Textured", out uint texProgram))
            {
                _gl.UseProgram(texProgram);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, textureId);
                int texLoc = _gl.GetUniformLocation(texProgram, "uTexture");
                if (texLoc != -1)
                    _gl.Uniform1(texLoc, 0);
            }
            else
            {
                Console.WriteLine("警告：未找到纹理着色器，无法绘制纹理。");
                _gl.DeleteVertexArray(vao);
                _gl.DeleteBuffer(vbo);
                _gl.DeleteBuffer(ebo);
                return;
            }

            _gl.DrawElements(PrimitiveType.Triangles, 6, DrawElementsType.UnsignedInt, 0);

            _gl.UseProgram(prevProgram);
            _gl.DeleteVertexArray(vao);
            _gl.DeleteBuffer(vbo);
            _gl.DeleteBuffer(ebo);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        // ==================== UI 绘制方法 ====================

        /// <summary>
        /// 将屏幕像素坐标转换为 NDC 坐标
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
        /// 绘制一个 UI 元素树
        /// </summary>
        public void DrawUI(UIElement root)
        {
            DrawUIElement(root);
        }

        /// <summary>
        /// 递归绘制 UI 元素
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
                        uint texId = LoadTexture(fullPath);
                        if (texId != 0)
                        {
                            (float x1, float y1) = PixelToNDC(element.X, element.Y);
                            (float x2, float y2) = PixelToNDC(element.X + element.Width, element.Y + element.Height);
                            DrawTexturedQuad(x1, y1, x2, y2, texId);
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

        /// <summary>
        /// 添加一个顶点到缓冲区
        /// </summary>
        private void AddVertex(float x, float y, float z)
        {
            _vertexBuffer.Add(x);
            _vertexBuffer.Add(y);
            _vertexBuffer.Add(z);
            _vertexBuffer.Add(_currentColor.X);
            _vertexBuffer.Add(_currentColor.Y);
            _vertexBuffer.Add(_currentColor.Z);
            _vertexBuffer.Add(_currentColor.W);
        }

        /// <summary>
        /// 刷新缓冲区到GPU并绘制
        /// </summary>
        private void Flush(PrimitiveType primitiveType)
        {
            if (_vertexBuffer.Count == 0) return;
            if (!_isInitialized) Initialize();

            var vertices = _vertexBuffer.ToArray();

            _gl.BindVertexArray(_vertexArrayObject);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertexBufferObject);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)vertices, BufferUsageARB.DynamicDraw);

            // 重新设置顶点属性指针
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            // 绑定着色器程序
            _gl.UseProgram(_currentProgram);

            _gl.DrawArrays(primitiveType, 0, (uint)(_vertexBuffer.Count / 7));

            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
            _gl.BindVertexArray(0);
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
