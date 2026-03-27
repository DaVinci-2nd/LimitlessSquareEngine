using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    // UI元素类型
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CanvasElementType
    {
        Panel,
        Button,
        TextBlock,
        Image,
        ColorBlock
    }

    // 布局模式
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CanvasLayoutMode
    {
        None,
        Vertical,
        Horizontal
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CanvasTextHorizontalAlign
    {
        Left,
        Center,
        Right
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum CanvasTextVerticalAlign
    {
        Top,
        Middle,
        Bottom
    }

    // UI元素类
    [MoonSharpUserData]
    public class CanvasElement
    {
        // 元素类型
        public CanvasElementType Type { get; set; }

        // 图层
        public int Layer { get; set; }

        // 本地坐标
        public float X { get; set; }
        public float Y { get; set; }

        // 尺寸
        public float Width { get; set; }
        public float Height { get; set; }

        // 可见性
        public bool Visible { get; set; }

        // 父元素
        [JsonIgnore]
        public CanvasElement? Parent { get; set; }

        // 子元素
        public List<CanvasElement> Children { get; set; } = new();

        // 布局模式
        public CanvasLayoutMode Layout { get; set; }

        // 内边距
        public float PaddingLeft { get; set; }
        public float PaddingTop { get; set; }
        public float PaddingRight { get; set; }
        public float PaddingBottom { get; set; }

        // 其它属性
        public Vector4 FillColor { get; set; }

        public string Content { get; set; }
        public Vector4 TextColor { get; set; }

        // 字体家族名，空则系统默认
        public string FontFamily { get; set; }

        // 字号（像素）
        public float FontSize { get; set; }

        public bool FontBold { get; set; }
        public bool FontItalic { get; set; }

        // 是否自动换行
        public bool WordWrap { get; set; }

        // 是否裁剪到元素矩形内
        public bool ClipText { get; set; }

        public CanvasTextHorizontalAlign TextHorizontalAlign { get; set; }
        public CanvasTextVerticalAlign TextVerticalAlign { get; set; }

        public string ImageSource { get; set; }

        public Vector4 TintColor { get; set; }

        public string ButtonId { get; set; }

        public bool Interactable { get; set; }

        public CanvasElement()
        {
            Visible = true;
            Layer = 0;

            X = 0f;
            Y = 0f;
            Width = 0f;
            Height = 0f;

            Layout = CanvasLayoutMode.None;

            PaddingLeft = 0f;
            PaddingTop = 0f;
            PaddingRight = 0f;
            PaddingBottom = 0f;

            FillColor = new Vector4(1f, 1f, 1f, 1f);

            Content = string.Empty;
            TextColor = new Vector4(1f, 1f, 1f, 1f);

            ImageSource = string.Empty;
            TintColor = new Vector4(1f, 1f, 1f, 1f);

            ButtonId = string.Empty;
            Interactable = true;
        }

        public void AddChild(CanvasElement child)
        {
            if (child == null)
                return;

            if (child.Parent != null)
                child.Parent.RemoveChild(child);

            child.Parent = this;
            Children.Add(child);
        }

        public void RemoveChild(CanvasElement child)
        {
            if (child == null)
                return;

            if (Children.Remove(child))
                child.Parent = null;
        }

        public float GetGlobalX()
        {
            if (Parent == null)
                return X;

            return Parent.GetGlobalX() + X;
        }

        public float GetGlobalY()
        {
            if (Parent == null)
                return Y;

            return Parent.GetGlobalY() + Y;
        }

        public bool ContainsPoint(float px, float py)
        {
            float gx = GetGlobalX();
            float gy = GetGlobalY();

            return px >= gx &&
                   py >= gy &&
                   px <= gx + Width &&
                   py <= gy + Height;
        }

        public void PerformLayout()
        {
            if (!Visible || Children.Count == 0)
                return;

            float currentX = PaddingLeft;
            float currentY = PaddingTop;

            switch (Layout)
            {
                case CanvasLayoutMode.Vertical:
                    foreach (CanvasElement child in Children)
                    {
                        if (!child.Visible)
                            continue;

                        child.X = currentX;
                        child.Y = currentY;

                        child.PerformLayout();

                        currentY += child.Height;
                    }
                    break;

                case CanvasLayoutMode.Horizontal:
                    foreach (CanvasElement child in Children)
                    {
                        if (!child.Visible)
                            continue;

                        child.X = currentX;
                        child.Y = currentY;

                        child.PerformLayout();

                        currentX += child.Width;
                    }
                    break;

                case CanvasLayoutMode.None:
                default:
                    foreach (CanvasElement child in Children)
                    {
                        if (!child.Visible)
                            continue;

                        child.PerformLayout();
                    }
                    break;
            }
        }
    }

    [MoonSharpUserData]
    internal class Canvas
    {
        public List<CanvasElement> RootElements { get; private set; }

        public Canvas()
        {
            RootElements = new List<CanvasElement>();
        }

        public void AddElement(CanvasElement element)
        {
            if (element == null)
                return;

            if (element.Parent != null)
                element.Parent.RemoveChild(element);

            element.Parent = null;
            RootElements.Add(element);
        }

        public void RemoveElement(CanvasElement element)
        {
            if (element == null)
                return;

            if (RootElements.Remove(element))
                element.Parent = null;
        }

        public void Clear()
        {
            foreach (CanvasElement element in RootElements)
                element.Parent = null;

            RootElements.Clear();
        }

        public void UpdateLayout()
        {
            foreach (CanvasElement element in RootElements)
            {
                if (!element.Visible)
                    continue;

                element.PerformLayout();
            }
        }
    }
}
