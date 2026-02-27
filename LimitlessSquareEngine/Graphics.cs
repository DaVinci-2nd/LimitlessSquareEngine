using MoonSharp.Interpreter;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using System.Numerics;
namespace LimitlessSquareEngine
{
    internal class Graphics
    {
        private GL _gl;
        private IWindow _window;

        //顶点数据缓存
        private List<float> _vertexBuffer = new List<float>();
        private uint _vertexArrayObject;
        private uint _vertexBufferObject;
        private uint _shaderProgram;
        private bool _isInitialized = false;

        //当前绘制颜色
        private Vector4 _currentColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);

        //背景色
        private Vector4 _backgroundColor = new Vector4(0.2f, 0.2f, 0.2f, 1.0f);


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

            _shaderProgram = CreateShaderProgram();

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
                throw new Exception($"Shader compilation failed: {infoLog}");
            }
            return shader;
        }

        /// <summary>
        /// 创建着色器程序
        /// </summary>
        private uint CreateShaderProgram()
        {
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


        /// <summary>
        /// 设置当前绘制颜色 (RGBA, 每个分量0-1)
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
        /// 设置背景色（供Lua调用）
        /// </summary>
        public void SetBackgroundColor(float r, float g, float b, float a = 1.0f)
        {
            _backgroundColor = new Vector4(r, g, b, a);
        }

        /// <summary>
        /// 执行清屏（仅引擎调用，隐藏）
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

            // 重新设置顶点属性指针（确保状态正确）
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 7 * sizeof(float), 0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 7 * sizeof(float), 3 * sizeof(float));
            _gl.EnableVertexAttribArray(1);

            // 绑定着色器程序
            _gl.UseProgram(_shaderProgram);

            _gl.DrawArrays(primitiveType, 0, (uint)(_vertexBuffer.Count / 7));

            _gl.UseProgram(0);
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
