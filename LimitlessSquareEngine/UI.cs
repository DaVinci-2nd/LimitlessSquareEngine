using MoonSharp.Interpreter;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine
{
    // UI元素类型
    public enum UIElementType
    {
        Panel,
        Button,
        Label,
        Image
    }

    // 布局模式
    public enum LayoutMode
    {
        None,
        Vertical,
        Horizontal
    }
    // UI元素类
    [MoonSharpUserData]
    public class UIElement
    {
        // 元素类型
        public UIElementType Type { get; set; }

        // X坐标
        public float X { get; set; }

        // Y坐标（
        public float Y { get; set; }

        // 宽度
        public float Width { get; set; }

        // 高度
        public float Height { get; set; }

        // 背景颜色（R,G,B,A）
        public Vector4 BackgroundColor { get; set; }

        // 文本颜色
        public Vector4 TextColor { get; set; }

        // 显示的文本
        public string Text { get; set; }

        // 图片路径或标识（仅对Image有效）
        public string ImageSource { get; set; }

        // 是否可见
        public bool Visible { get; set; }

        // 父元素
        public UIElement Parent { get; set; }

        // 子元素列表
        public List<UIElement> Children { get; private set; }

        // 布局模式
        public LayoutMode Layout { get; set; }

        // 左边距
        public float PaddingLeft { get; set; }

        // 右边距
        public float PaddingRight { get; set; }

        // 上边距
        public float PaddingTop { get; set; }

        // 下边距
        public float PaddingBottom { get; set; }

        // UI构造函数
        public UIElement()
        {
            Children = new List<UIElement>();
            Visible = true;
            BackgroundColor = new Vector4(0.5f, 0.5f, 0.5f, 1.0f);
            TextColor = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            Text = string.Empty;
            ImageSource = string.Empty;
            Layout = LayoutMode.None;
        }

        // 添加子元素
        public void AddChild(UIElement child)
        {
            if (child == null) return;
            child.Parent = this;
            Children.Add(child);
        }

        // 移除子元素
        public void RemoveChild(UIElement child)
        {
            if (Children.Remove(child))
            {
                child.Parent = null;
            }
        }

        // 执行布局
        public void PerformLayout()
        {
            if (!Visible || Children.Count == 0) return;

            float currentX = X + PaddingLeft;
            float currentY = Y + PaddingTop;

            if (Layout == LayoutMode.Vertical)
            {
                foreach (var child in Children)
                {
                    if (!child.Visible) continue;
                    child.X = currentX;
                    child.Y = currentY;
                    child.PerformLayout();
                    currentY += child.Height + PaddingBottom;
                }
            }
            else if (Layout == LayoutMode.Horizontal)
            {
                foreach (var child in Children)
                {
                    if (!child.Visible) continue;
                    child.X = currentX;
                    child.Y = currentY;
                    child.PerformLayout();
                    currentX += child.Width + PaddingRight;
                }
            }
            else
            {
                foreach (var child in Children)
                {
                    child.PerformLayout();
                }
            }
        }
    }

    [MoonSharpUserData]
    internal class UI
    {
        // 根元素列表
        public List<UIElement> RootElements { get; private set; }

        // 构造函数
        public UI()
        {
            RootElements = new List<UIElement>();
        }

        // 添加根元素
        public void AddElement(UIElement element)
        {
            if (element.Parent != null)
            {
                element.Parent.RemoveChild(element);
            }
            RootElements.Add(element);
        }

        // 移除根元素
        public void RemoveElement(UIElement element)
        {
            RootElements.Remove(element);
        }

        // 更新所有元素的布局
        public void UpdateLayout()
        {
            foreach (var element in RootElements)
            {
                element.PerformLayout();
            }
        }
    }
}
