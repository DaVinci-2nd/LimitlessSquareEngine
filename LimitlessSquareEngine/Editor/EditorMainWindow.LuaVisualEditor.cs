using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace LimitlessSquareEngine.Editor
{
    public sealed partial class EditorMainWindow
    {
        private enum LuaBlockType
        {
            Custom,
            Comment,
            Local,
            Assignment,
            If,
            ElseIf,
            Else,
            For,
            While,
            Repeat,
            Until,
            Function,
            Do,
            End,
            Return,
            Break,
            Empty
        }

        private sealed class LuaBlock
        {
            public LuaBlockType Type;
            public string Text = "";
            public int Indent;
        }

        private List<LuaBlock> _luaVisualBlocks = new();
        private ScrollViewer? _luaVisualScrollViewer;
        private StackPanel? _luaVisualBlockPanel;
        private TextEditor? _luaSourceEditor;

        private bool _luaBlockIsDragging;
        private int _luaDragBlockSourceIndex = -1;
        private Border? _luaDragSourceControl;
        private double _luaDragStartY;
        private bool _luaDragHasMoved;

        private Canvas? _luaDragGhostCanvas;
        private Control? _luaDragGhostControl;
        private Border? _luaDragInsertIndicator;
        private int _luaDragLastInsertIndex = -1;
        private LuaBlock? _luaDragSavedBlock;

        private bool _luaIsSyncingToSource;

        private const double LuaBlockHeight = 50.0;
        private const double LuaBlockSubRowHeight = 26.0;
        private const double LuaLineNumberWidth = 52.0;
        private const double LuaTypeBadgeWidth = 80.0;
        private const double LuaDragGripWidth = 14.0;

        private static Color LuaColorParse(string hex) => Color.Parse(hex);

        private static Color LuaColorDarken(Color c)
        {
            return Color.FromRgb(
                (byte)(c.R * 0.25),
                (byte)(c.G * 0.25),
                (byte)(c.B * 0.25));
        }

        private static Color GetBlockBorderColor(LuaBlockType type)
        {
            return type switch
            {
                LuaBlockType.Comment => LuaColorParse("#6A9955"),
                LuaBlockType.If or LuaBlockType.ElseIf or LuaBlockType.Else
                    or LuaBlockType.For or LuaBlockType.While
                    or LuaBlockType.Repeat or LuaBlockType.Until
                    or LuaBlockType.Function or LuaBlockType.Do
                    or LuaBlockType.End or LuaBlockType.Return
                    or LuaBlockType.Break or LuaBlockType.Local => LuaColorParse("#569CD6"),
                LuaBlockType.Assignment => LuaColorParse("#9CDCFE"),
                LuaBlockType.Empty => LuaColorParse("#444444"),
                _ => LuaColorParse("#555555"),
            };
        }

        private static Color GetBlockFillColor(LuaBlockType type)
        {
            return LuaColorDarken(GetBlockBorderColor(type));
        }

        private static string GetBlockTypeLabel(LuaBlockType type)
        {
            return type switch
            {
                LuaBlockType.Comment => "--\u6CE8\u91CA",
                LuaBlockType.Local => "local",
                LuaBlockType.Assignment => "\u8D4B\u503C",
                LuaBlockType.If => "if",
                LuaBlockType.ElseIf => "elseif",
                LuaBlockType.Else => "else",
                LuaBlockType.For => "for",
                LuaBlockType.While => "while",
                LuaBlockType.Repeat => "repeat",
                LuaBlockType.Until => "until",
                LuaBlockType.Function => "function",
                LuaBlockType.Do => "do",
                LuaBlockType.End => "end",
                LuaBlockType.Return => "return",
                LuaBlockType.Break => "break",
                LuaBlockType.Empty => "",
                _ => "\u81EA\u5B9A\u4E49",
            };
        }

        private static LuaBlockType DetectBlockType(string trimmedLine)
        {
            if (string.IsNullOrEmpty(trimmedLine))
                return LuaBlockType.Empty;
            if (trimmedLine.StartsWith("--"))
                return LuaBlockType.Comment;
            if (Regex.IsMatch(trimmedLine, @"^local\s"))
                return LuaBlockType.Local;
            if (Regex.IsMatch(trimmedLine, @"^if\s"))
                return LuaBlockType.If;
            if (Regex.IsMatch(trimmedLine, @"^elseif\s"))
                return LuaBlockType.ElseIf;
            if (trimmedLine == "else")
                return LuaBlockType.Else;
            if (Regex.IsMatch(trimmedLine, @"^for\s"))
                return LuaBlockType.For;
            if (Regex.IsMatch(trimmedLine, @"^while\s"))
                return LuaBlockType.While;
            if (trimmedLine == "repeat")
                return LuaBlockType.Repeat;
            if (Regex.IsMatch(trimmedLine, @"^until\s"))
                return LuaBlockType.Until;
            if (Regex.IsMatch(trimmedLine, @"^function\s"))
                return LuaBlockType.Function;
            if (trimmedLine == "do")
                return LuaBlockType.Do;
            if (trimmedLine == "end")
                return LuaBlockType.End;
            if (trimmedLine == "break")
                return LuaBlockType.Break;
            if (Regex.IsMatch(trimmedLine, @"^return[\s(]"))
                return LuaBlockType.Return;
            if (Regex.IsMatch(trimmedLine, @"^\w[\w,\s]*\s*="))
                return LuaBlockType.Assignment;
            return LuaBlockType.Custom;
        }

        private static int DetectIndent(string line)
        {
            int count = 0;
            foreach (char c in line)
            {
                if (c == ' ') count++;
                else if (c == '\t') count += 4;
                else break;
            }
            return count;
        }

        private List<LuaBlock> ParseLuaToBlocks(string text)
        {
            var blocks = new List<LuaBlock>();
            if (string.IsNullOrEmpty(text))
            {
                blocks.Add(new LuaBlock { Type = LuaBlockType.Empty, Text = "", Indent = 0 });
                return blocks;
            }
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();
                int indent = DetectIndent(line);
                LuaBlockType type = DetectBlockType(trimmed);
                blocks.Add(new LuaBlock { Type = type, Text = trimmed, Indent = indent });
            }
            return blocks;
        }

        private string GenerateLuaFromBlocks()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _luaVisualBlocks.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                LuaBlock block = _luaVisualBlocks[i];
                sb.Append(new string(' ', block.Indent));
                sb.Append(block.Text);
            }
            return sb.ToString();
        }

        private Control BuildLuaVisualEditor(string filePath)
        {
            string content;
            try { content = File.ReadAllText(filePath); }
            catch { content = ""; }

            _luaVisualBlocks = ParseLuaToBlocks(content);

            var lineNumberStack = new StackPanel { Orientation = Orientation.Vertical };
            _luaVisualBlockPanel = new StackPanel { Orientation = Orientation.Vertical };
            _luaVisualBlockPanel.PointerMoved += OnLuaBlockPanelPointerMoved;
            _luaVisualBlockPanel.PointerReleased += OnLuaBlockPanelPointerReleased;

            PopulateLuaVisualRows(lineNumberStack, _luaVisualBlockPanel);

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions($"{LuaLineNumberWidth},*")
            };
            Grid.SetColumn(lineNumberStack, 0);
            Grid.SetColumn(_luaVisualBlockPanel, 1);
            grid.Children.Add(lineNumberStack);
            grid.Children.Add(_luaVisualBlockPanel);

            _luaDragGhostCanvas = new Canvas
            {
                IsHitTestVisible = false,
                ZIndex = 100
            };
            Grid.SetColumn(_luaDragGhostCanvas, 1);
            grid.Children.Add(_luaDragGhostCanvas);

            _luaVisualScrollViewer = new ScrollViewer
            {
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Content = grid
            };

            return _luaVisualScrollViewer;
        }

        private void PopulateLuaVisualRows(StackPanel lineNumberStack, StackPanel blockStack)
        {
            int count = _luaVisualBlocks.Count;
            if (count == 0)
            {
                _luaVisualBlocks.Add(new LuaBlock { Type = LuaBlockType.Empty, Text = "", Indent = 0 });
                count = 1;
            }
            for (int i = 0; i < count; i++)
            {
                var (control, height) = BuildBlockControl(_luaVisualBlocks[i], i);
                lineNumberStack.Children.Add(BuildLineNumberCell(i + 1, height));
                blockStack.Children.Add(control);
            }
            blockStack.Children.Add(BuildAddBlockButton());
        }

        private Border BuildLineNumberCell(int lineNumber, double height)
        {
            return new Border
            {
                Height = height,
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                Child = new TextBlock
                {
                    Text = lineNumber.ToString(),
                    Foreground = new SolidColorBrush(Color.Parse("#858585")),
                    FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                }
            };
        }

        private static TextBox CreateLuaInlineTextBox(string text, int blockIndex, Action<int, string> onChanged)
        {
            var tb = new TextBox
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0),
                MinWidth = 20,
                VerticalContentAlignment = VerticalAlignment.Center,
                Tag = blockIndex
            };
            var lastCommitted = text;
            tb.LostFocus += (s, _) =>
            {
                if (s is TextBox box && box.Text != null && box.Text != lastCommitted)
                {
                    lastCommitted = box.Text;
                    onChanged(blockIndex, box.Text);
                }
            };
            tb.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && s is TextBox box && box.Text != null && box.Text != lastCommitted)
                {
                    lastCommitted = box.Text;
                    onChanged(blockIndex, box.Text);
                }
            };
            return tb;
        }

        private static TextBlock CreateLuaLabel(string text, Color color)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(color),
                FontSize = 13,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
        }

        private static TextBlock CreateLuaMutedLabel(string text)
        {
            return CreateLuaLabel(text, Color.Parse("#808080"));
        }

        private Border BuildDragGrip(int index, Border parentBlockBorder)
        {
            var grip = new Border
            {
                Width = LuaDragGripWidth,
                MinWidth = LuaDragGripWidth,
                Background = Brushes.Transparent,
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
                Tag = index,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = "\u22EE",
                    Foreground = new SolidColorBrush(Color.Parse("#555555")),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    IsHitTestVisible = false
                }
            };

            grip.PointerPressed += (s, e) =>
            {
                if (s is not Border g) return;
                var props = e.GetCurrentPoint(g);
                if (!props.Properties.IsLeftButtonPressed) return;
                int idx = g.Tag is int id ? id : -1;
                if (idx < 0 || idx >= _luaVisualBlocks.Count) return;
                if (_luaVisualBlockPanel == null || _luaDragGhostCanvas == null) return;

                ResetLuaDragState();

                _luaDragBlockSourceIndex = idx;
                _luaDragHasMoved = false;
                _luaDragStartY = e.GetPosition(_luaVisualBlockPanel).Y;
                _luaDragSourceControl = g;

                _luaDragSavedBlock = _luaVisualBlocks[idx];
                _luaVisualBlocks.RemoveAt(idx);

                var ghost = parentBlockBorder;
                _luaVisualBlockPanel.Children.RemoveAt(idx);

                _luaDragGhostControl = ghost;
                ghost.Opacity = 0.85;
                ghost.ZIndex = 10;
                double ghostY = (idx) * (LuaBlockHeight + 2);
                Canvas.SetLeft(ghost, 0);
                Canvas.SetTop(ghost, ghostY);
                _luaDragGhostCanvas.Children.Add(ghost);

                g.Cursor = new Cursor(StandardCursorType.DragMove);
                e.Pointer.Capture(g);
                e.Handled = true;
            };

            grip.PointerMoved += (s, e) =>
            {
                if (_luaDragBlockSourceIndex < 0 || _luaDragGhostControl == null || _luaDragSourceControl == null) return;
                var props = e.GetCurrentPoint(_luaDragSourceControl);
                if (!props.Properties.IsLeftButtonPressed)
                {
                    CancelLuaDrag();
                    return;
                }

                double currentY = e.GetPosition(_luaVisualBlockPanel).Y;
                double deltaY = currentY - _luaDragStartY;
                if (!_luaDragHasMoved && Math.Abs(deltaY) < 3) return;

                _luaDragHasMoved = true;
                _luaBlockIsDragging = true;

                double ghostTop = currentY - (parentBlockBorder.Height / 2);
                Canvas.SetTop(_luaDragGhostControl, ghostTop);

                int targetIndex = (int)Math.Round(currentY / (LuaBlockHeight + 2));
                targetIndex = Math.Clamp(targetIndex, 0, _luaVisualBlocks.Count);

                if (targetIndex != _luaDragLastInsertIndex)
                {
                    RemoveDragInsertIndicator();
                    _luaDragLastInsertIndex = targetIndex;

                    Color indicatorColor = GetBlockBorderColor(_luaDragSavedBlock.Type);
                    _luaDragInsertIndicator = new Border
                    {
                        Height = 3,
                        Background = new SolidColorBrush(indicatorColor),
                        Margin = new Thickness(0, 1, 0, 1),
                        IsHitTestVisible = false
                    };
                    _luaVisualBlockPanel!.Children.Insert(targetIndex, _luaDragInsertIndicator);
                }

                e.Handled = true;
            };

            grip.PointerReleased += (s, e) =>
            {
                if (_luaDragBlockSourceIndex < 0) return;
                e.Pointer.Capture(null);

                int finalIndex = _luaDragLastInsertIndex >= 0
                    ? _luaDragLastInsertIndex
                    : (int)Math.Round(e.GetPosition(_luaVisualBlockPanel).Y / (LuaBlockHeight + 2));
                finalIndex = Math.Clamp(finalIndex, 0, _luaVisualBlocks.Count);

                if (_luaDragSavedBlock != null)
                    _luaVisualBlocks.Insert(finalIndex, _luaDragSavedBlock);

                CleanupLuaDrag();
                RebuildLuaVisualView();
                SyncVisualToSource();
                e.Handled = true;
            };

            return grip;
        }

        private Border BuildTypeBadge(Color borderColor, string label, int blockIndex)
        {
            var badge = new Border
            {
                Width = LuaTypeBadgeWidth,
                MinWidth = LuaTypeBadgeWidth,
                Background = new SolidColorBrush(borderColor),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 2),
                Margin = new Thickness(6, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = blockIndex,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
            badge.PointerPressed += (s, e) =>
            {
                if (s is not Border b) return;
                var props = e.GetCurrentPoint(b);
                if (!props.Properties.IsLeftButtonPressed) return;
                int idx = b.Tag is int id ? id : -1;
                if (idx < 0 || idx >= _luaVisualBlocks.Count) return;
                OnTypeBadgeClicked(b, idx);
                e.Handled = true;
            };
            return badge;
        }

        private void OnTypeBadgeClicked(Control placementTarget, int blockIndex)
        {
            var typeEntries = new (LuaBlockType Type, string Label, Color Color)[]
            {
                (LuaBlockType.Custom, "自定义", GetBlockBorderColor(LuaBlockType.Custom)),
                (LuaBlockType.Comment, "--注释", GetBlockBorderColor(LuaBlockType.Comment)),
                (LuaBlockType.Local, "local", GetBlockBorderColor(LuaBlockType.Local)),
                (LuaBlockType.Assignment, "赋值", GetBlockBorderColor(LuaBlockType.Assignment)),
                (LuaBlockType.If, "if", GetBlockBorderColor(LuaBlockType.If)),
                (LuaBlockType.ElseIf, "elseif", GetBlockBorderColor(LuaBlockType.ElseIf)),
                (LuaBlockType.Else, "else", GetBlockBorderColor(LuaBlockType.Else)),
                (LuaBlockType.For, "for", GetBlockBorderColor(LuaBlockType.For)),
                (LuaBlockType.While, "while", GetBlockBorderColor(LuaBlockType.While)),
                (LuaBlockType.Repeat, "repeat", GetBlockBorderColor(LuaBlockType.Repeat)),
                (LuaBlockType.Until, "until", GetBlockBorderColor(LuaBlockType.Until)),
                (LuaBlockType.Function, "function", GetBlockBorderColor(LuaBlockType.Function)),
                (LuaBlockType.Do, "do", GetBlockBorderColor(LuaBlockType.Do)),
                (LuaBlockType.End, "end", GetBlockBorderColor(LuaBlockType.End)),
                (LuaBlockType.Return, "return", GetBlockBorderColor(LuaBlockType.Return)),
                (LuaBlockType.Break, "break", GetBlockBorderColor(LuaBlockType.Break)),
            };

            var listPanel = new StackPanel
            {
                Background = new SolidColorBrush(Color.Parse("#252525")),
                MinWidth = 160,
                MaxHeight = 400
            };

            var scrollViewer = new ScrollViewer { Content = listPanel };

            foreach (var (type, label, color) in typeEntries)
            {
                var item = new Button
                {
                    Height = 26,
                    Padding = new Thickness(8, 2),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Cursor = new Cursor(StandardCursorType.Hand),
                    Content = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new Border
                            {
                                Width = 14, Height = 14,
                                Background = new SolidColorBrush(color),
                                CornerRadius = new CornerRadius(2)
                            },
                            new TextBlock
                            {
                                Text = label,
                                Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                                FontSize = 12,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                };

                var capturedType = type;
                item.Click += (_, _) =>
                {
                    _luaVisualBlocks[blockIndex].Type = capturedType;
                    _luaVisualBlocks[blockIndex].Text = "";
                    RebuildLuaVisualView();
                    SyncVisualToSource();
                };

                listPanel.Children.Add(item);
            }

            var popup = new Popup
            {
                PlacementTarget = placementTarget,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                IsLightDismissEnabled = true,
                Child = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(Color.Parse("#252525")),
                    Child = scrollViewer
                }
            };

            popup.Closed += (_, _) => { };
            popup.Open();
        }

        private (Control control, double height) BuildBlockControl(LuaBlock block, int index)
        {
            Color borderColor = GetBlockBorderColor(block.Type);
            Color fillColor = GetBlockFillColor(block.Type);
            string label = GetBlockTypeLabel(block.Type);

            var contentStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            double blockHeight = LuaBlockHeight;

            switch (block.Type)
            {
                case LuaBlockType.If:
                case LuaBlockType.ElseIf:
                    blockHeight = BuildIfBlockContent(block, index, borderColor, contentStack);
                    break;
                case LuaBlockType.Local:
                case LuaBlockType.Assignment:
                    BuildAssignmentBlockContent(block, index, borderColor, contentStack);
                    break;
                case LuaBlockType.Custom:
                    BuildCustomBlockContent(block, index, contentStack);
                    break;
                default:
                    BuildDefaultBlockContent(block, index, borderColor, contentStack);
                    break;
            }

            var blockBorder = new Border
            {
                Height = blockHeight,
                BorderThickness = new Thickness(4, 0, 0, 0),
                BorderBrush = new SolidColorBrush(borderColor),
                Background = new SolidColorBrush(fillColor),
                Margin = new Thickness(0, 0, 0, 2),
                Padding = new Thickness(0),
                RenderTransform = new TranslateTransform(0, 0)
            };

            var grip = BuildDragGrip(index, blockBorder);
            var badge = BuildTypeBadge(borderColor, label, index);

            var outerStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Stretch,
                Children = { grip, badge, contentStack }
            };

            blockBorder.Child = outerStack;
            return (blockBorder, blockHeight);
        }

        private void BuildDefaultBlockContent(LuaBlock block, int index, Color borderColor, StackPanel parent)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var indentSpacer = new Border
            {
                Width = block.Indent > 0 ? block.Indent * 1.0 : 0,
                IsHitTestVisible = false
            };
            row.Children.Add(indentSpacer);

            if (block.Type == LuaBlockType.Comment)
            {
                row.Children.Add(CreateLuaMutedLabel("--"));

                string commentText = block.Text;
                if (commentText.StartsWith("--"))
                    commentText = commentText.Length > 2 ? commentText.Substring(2) : "";

                var textBox = CreateLuaInlineTextBox(commentText, index, (idx, newText) =>
                {
                    _luaVisualBlocks[idx].Text = "--" + newText;
                    SyncVisualToSource();
                });
                textBox.MinWidth = 200;
                row.Children.Add(textBox);
            }
            else
            {
                string displayText = block.Text;
                if (block.Type == LuaBlockType.Empty || string.IsNullOrEmpty(block.Text))
                    displayText = "\u00A0";

                row.Children.Add(new TextBlock
                {
                    Text = displayText,
                    Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                    FontSize = 13,
                    FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                });
            }

            parent.Children.Add(row);
        }

        private void BuildCustomBlockContent(LuaBlock block, int index, StackPanel parent)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var indentSpacer = new Border
            {
                Width = block.Indent > 0 ? block.Indent * 1.0 : 0,
                IsHitTestVisible = false
            };
            row.Children.Add(indentSpacer);

            var textBox = CreateLuaInlineTextBox(block.Text, index, (idx, newText) =>
            {
                _luaVisualBlocks[idx].Text = newText;
                LuaBlockType detected = DetectBlockType(newText.TrimStart());
                _luaVisualBlocks[idx].Type = detected;
                SyncVisualToSource();
            });
            textBox.MinWidth = 200;
            row.Children.Add(textBox);

            parent.Children.Add(row);
        }

        private void BuildAssignmentBlockContent(LuaBlock block, int index, Color borderColor, StackPanel parent)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var indentSpacer = new Border
            {
                Width = block.Indent > 0 ? block.Indent * 1.0 : 0,
                IsHitTestVisible = false
            };
            row.Children.Add(indentSpacer);

            bool isLocal = block.Type == LuaBlockType.Local;
            string workText = block.Text;

            string prefix = "";
            if (isLocal && workText.StartsWith("local "))
            {
                prefix = "local ";
                workText = workText.Substring(6);
            }

            if (!string.IsNullOrEmpty(prefix))
                row.Children.Add(CreateLuaMutedLabel(prefix));

            int eqIdx = workText.IndexOf('=');
            string lhs = eqIdx >= 0 ? workText.Substring(0, eqIdx).TrimEnd() : workText;
            string rhs = eqIdx >= 0 ? workText.Substring(eqIdx) : "";

            string[] varParts = lhs.Split(',');
            for (int vi = 0; vi < varParts.Length; vi++)
            {
                if (vi > 0)
                    row.Children.Add(CreateLuaMutedLabel(", "));

                string varName = varParts[vi].Trim();
                var varBox = CreateLuaInlineTextBox(varName, index, (idx, newText) =>
                {
                    RebuildAssignmentText(idx);
                    SyncVisualToSource();
                });
                row.Children.Add(varBox);
            }

            if (!string.IsNullOrEmpty(rhs))
                row.Children.Add(CreateLuaMutedLabel(" " + rhs));

            parent.Children.Add(row);
        }

        private void RebuildAssignmentText(int index)
        {
            var block = _luaVisualBlocks[index];
            bool isLocal = block.Type == LuaBlockType.Local;
            if (_luaVisualScrollViewer == null) return;
            var grid = _luaVisualScrollViewer.Content as Grid;
            if (grid == null) return;
            var panel = grid.Children.Count > 1 ? grid.Children[1] as StackPanel : null;
            if (panel == null || index >= panel.Children.Count) return;

            var control = panel.Children[index] as Border;
            if (control?.Child is not StackPanel outerStack) return;
            var contentStack = outerStack.Children.Count > 2 ? outerStack.Children[2] as StackPanel : null;
            if (contentStack == null || contentStack.Children.Count == 0) return;
            var row = contentStack.Children[0] as StackPanel;
            if (row == null) return;

            var sb = new System.Text.StringBuilder();
            if (isLocal) sb.Append("local ");

            var varNames = new List<string>();
            for (int ci = isLocal ? 1 : 0; ci < row.Children.Count; ci++)
            {
                if (row.Children[ci] is TextBox tb && tb.Tag is int)
                    varNames.Add(tb.Text ?? "");
            }

            sb.Append(string.Join(", ", varNames));

            int lastLabelIdx = row.Children.Count - 1;
            if (lastLabelIdx >= 0 && row.Children[lastLabelIdx] is TextBlock lastLabel)
            {
                string trimText = (lastLabel.Text ?? "").TrimStart();
                if (trimText.StartsWith("=") || trimText.StartsWith(" ="))
                    sb.Append(" " + trimText);
            }

            block.Text = sb.ToString().Trim();
        }

        private double BuildIfBlockContent(LuaBlock block, int index, Color borderColor, StackPanel parent)
        {
            string prefix = block.Type == LuaBlockType.If ? "if " : "elseif ";
            string suffix = " then";

            string workText = block.Text;
            if (workText.StartsWith(prefix))
                workText = workText.Substring(prefix.Length);
            if (workText.EndsWith(suffix))
                workText = workText.Substring(0, workText.Length - suffix.Length);

            var (conditions, connectors) = ParseConditions(workText.Trim());

            var mainRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, conditions.Count > 1 ? 1 : 0)
            };

            var indentSpacer = new Border
            {
                Width = block.Indent > 0 ? block.Indent * 1.0 : 0,
                IsHitTestVisible = false
            };
            mainRow.Children.Add(indentSpacer);
            mainRow.Children.Add(CreateLuaMutedLabel(prefix));

            for (int ci = 0; ci < conditions.Count; ci++)
            {
                int condIdx = ci;
                var condBox = CreateLuaInlineTextBox(conditions[ci], index, (idx, newText) =>
                {
                    RebuildIfConditionText(idx);
                    SyncVisualToSource();
                });
                mainRow.Children.Add(condBox);

                if (ci < conditions.Count - 1)
                    mainRow.Children.Add(CreateLuaMutedLabel(" " + connectors[ci] + " "));
            }

            mainRow.Children.Add(CreateLuaMutedLabel(suffix));
            parent.Children.Add(mainRow);

            for (int ci = 1; ci < conditions.Count; ci++)
            {
                var subRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, ci < conditions.Count - 1 ? 1 : 0)
                };

                double spacerWidth = (block.Indent > 0 ? block.Indent * 1.0 : 0) + LuaTypeBadgeWidth + LuaDragGripWidth + 12;
                var subSpacer = new Border { Width = spacerWidth, IsHitTestVisible = false };
                subRow.Children.Add(subSpacer);

                int condIdx = ci;
                subRow.Children.Add(CreateLuaMutedLabel(" " + connectors[ci - 1] + " "));

                var subCondBox = CreateLuaInlineTextBox(conditions[ci], index, (idx, newText) =>
                {
                    RebuildIfConditionText(idx);
                    SyncVisualToSource();
                });
                subRow.Children.Add(subCondBox);

                parent.Children.Add(subRow);
            }

            return LuaBlockHeight + Math.Max(0, conditions.Count - 1) * LuaBlockSubRowHeight;
        }

        private void RebuildIfConditionText(int index)
        {
            var block = _luaVisualBlocks[index];
            bool isIf = block.Type == LuaBlockType.If;
            string prefix = isIf ? "if " : "elseif ";
            string suffix = " then";

            if (_luaVisualScrollViewer == null) return;
            var grid = _luaVisualScrollViewer.Content as Grid;
            if (grid == null) return;
            var panel = grid.Children.Count > 1 ? grid.Children[1] as StackPanel : null;
            if (panel == null || index >= panel.Children.Count) return;
            var control = panel.Children[index] as Border;
            if (control?.Child is not StackPanel outerStack) return;
            var contentStack = outerStack.Children.Count > 2 ? outerStack.Children[2] as StackPanel : null;
            if (contentStack == null) return;

            var conditions = new List<string>();
            var connectors = new List<string>();

            foreach (var child in contentStack.Children)
            {
                if (child is not StackPanel row) continue;
                foreach (var el in row.Children)
                {
                    if (el is TextBox tb && tb.Tag is int)
                        conditions.Add(tb.Text ?? "");
                }
            }

            for (int ri = 0; ri < contentStack.Children.Count; ri++)
            {
                if (ri > 0)
                {
                    if (contentStack.Children[ri] is StackPanel subRow)
                    {
                        foreach (var el in subRow.Children)
                        {
                            if (el is TextBlock tblock && tblock.Text != null)
                            {
                                string t = tblock.Text.Trim();
                                if (t == "and" || t == "or")
                                    connectors.Add(t);
                            }
                        }
                    }
                }
            }

            if (connectors.Count < conditions.Count - 1)
            {
                while (connectors.Count < conditions.Count - 1)
                    connectors.Add("and");
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(prefix);
            sb.Append(conditions[0]);
            for (int ci = 0; ci < connectors.Count && ci + 1 < conditions.Count; ci++)
            {
                sb.Append(' ');
                sb.Append(connectors[ci]);
                sb.Append(' ');
                sb.Append(conditions[ci + 1]);
            }
            sb.Append(suffix);

            block.Text = sb.ToString();
        }

        private static (List<string> conditions, List<string> connectors) ParseConditions(string conditionText)
        {
            var conditions = new List<string>();
            var connectors = new List<string>();

            if (string.IsNullOrWhiteSpace(conditionText))
            {
                conditions.Add("");
                return (conditions, connectors);
            }

            var matches = Regex.Matches(conditionText, @"\s+(and|or)\s+", RegexOptions.IgnoreCase);
            if (matches.Count == 0)
            {
                conditions.Add(conditionText);
                return (conditions, connectors);
            }

            int lastEnd = 0;
            foreach (Match m in matches)
            {
                conditions.Add(conditionText.Substring(lastEnd, m.Index - lastEnd).Trim());
                connectors.Add(m.Groups[1].Value.ToLowerInvariant());
                lastEnd = m.Index + m.Length;
            }
            conditions.Add(conditionText.Substring(lastEnd).Trim());

            return (conditions, connectors);
        }

        private Control BuildAddBlockButton()
        {
            var addButton = new Button
            {
                Content = "+ \u6DFB\u52A0\u884C",
                Height = 30,
                FontSize = 12,
                Padding = new Thickness(8, 2),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                Foreground = new SolidColorBrush(Color.Parse("#AAAAAA")),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            addButton.Click += (_, _) => OnLuaAddBlock();
            return addButton;
        }

        private void RebuildLuaVisualView()
        {
            if (_luaVisualBlockPanel == null || _luaVisualScrollViewer == null)
                return;

            RemoveDragInsertIndicator();
            ResetLuaDragState();

            var grid = _luaVisualScrollViewer.Content as Grid;
            if (grid == null) return;

            var lineNumberStack = grid.Children.Count > 0 ? grid.Children[0] as StackPanel : null;
            if (lineNumberStack != null) lineNumberStack.Children.Clear();

            _luaVisualBlockPanel.Children.Clear();

            PopulateLuaVisualRows(
                lineNumberStack ?? new StackPanel(),
                _luaVisualBlockPanel);
        }

        private void OnLuaBlockPanelPointerMoved(object? sender, PointerEventArgs e) { }

        private void OnLuaBlockPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_luaBlockIsDragging && _luaDragBlockSourceIndex >= 0)
            {
                CancelLuaDrag();
            }
        }

        private void RemoveDragInsertIndicator()
        {
            if (_luaDragInsertIndicator != null && _luaVisualBlockPanel != null)
            {
                _luaVisualBlockPanel.Children.Remove(_luaDragInsertIndicator);
                _luaDragInsertIndicator = null;
            }
        }

        private void CleanupLuaDrag()
        {
            RemoveDragInsertIndicator();
            if (_luaDragGhostControl != null && _luaDragGhostCanvas != null)
            {
                _luaDragGhostCanvas.Children.Remove(_luaDragGhostControl);
                _luaDragGhostControl.Opacity = 1.0;
                _luaDragGhostControl.ZIndex = 0;
            }
            ResetLuaDragState();
        }

        private void CancelLuaDrag()
        {
            if (_luaDragSavedBlock != null)
                _luaVisualBlocks.Insert(_luaDragBlockSourceIndex, _luaDragSavedBlock);
            CleanupLuaDrag();
            RebuildLuaVisualView();
        }

        private void ResetLuaDragState()
        {
            if (_luaDragSourceControl != null)
                _luaDragSourceControl.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);

            _luaBlockIsDragging = false;
            _luaDragBlockSourceIndex = -1;
            _luaDragSourceControl = null;
            _luaDragGhostControl = null;
            _luaDragInsertIndicator = null;
            _luaDragLastInsertIndex = -1;
            _luaDragSavedBlock = null;
            _luaDragHasMoved = false;
        }

        private void OnLuaAddBlock()
        {
            _luaVisualBlocks.Add(new LuaBlock
            {
                Type = LuaBlockType.Custom,
                Text = "",
                Indent = 0
            });
            RebuildLuaVisualView();
            SyncVisualToSource();
        }

        private void SyncSourceToVisual()
        {
            if (_luaSourceEditor == null) return;
            _luaVisualBlocks = ParseLuaToBlocks(_luaSourceEditor.Text);
            RebuildLuaVisualView();
        }

        private void SyncVisualToSource()
        {
            if (_luaSourceEditor == null) return;
            _luaSourceEditor.Text = GenerateLuaFromBlocks();
        }

        private void SyncVisualToSourceSilent()
        {
            if (_luaSourceEditor == null) return;
            _luaIsSyncingToSource = true;
            _luaSourceEditor.Text = GenerateLuaFromBlocks();
            _luaIsSyncingToSource = false;
        }

        private bool IsLuaSyncingToSource()
        {
            return _luaIsSyncingToSource;
        }

        // ========================================
        // Lua Code Completion
        // ========================================

        private CompletionWindow? _luaCompletionWindow;
        private EditorHostBridge.LuaApiMetadata[] _luaApiMetaCache = Array.Empty<EditorHostBridge.LuaApiMetadata>();
        private static readonly string[] LuaKeywords =
        {
            "and", "break", "do", "else", "elseif", "end",
            "false", "for", "function", "goto", "if", "in",
            "local", "nil", "not", "or", "repeat", "return",
            "then", "true", "until", "while"
        };

        private void InitLuaCompletion(TextEditor editor)
        {
            _luaApiMetaCache = EditorHostBridge.GetLuaApiMetadata();

            editor.TextArea.TextEntered += OnLuaTextEntered;
            editor.TextArea.KeyDown += OnLuaKeyDown;
        }

        private void OnLuaKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                ShowLuaCompletionWindow();
            }
        }

        private void OnLuaTextEntered(object? sender, TextInputEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text))
                return;

            if (char.IsLetterOrDigit(e.Text[0]) || e.Text[0] == '_')
            {
                if (_luaCompletionWindow != null)
                    return;

                TextArea? textArea = sender as TextArea;
                if (textArea == null)
                    return;

                string currentWord = GetCurrentWord(textArea);
                if (currentWord.Length >= 2)
                {
                    ShowLuaCompletionWindow();
                }
            }
        }

        private void ShowLuaCompletionWindow()
        {
            TextArea? textArea = _luaSourceEditor?.TextArea;
            if (textArea == null)
                return;

            _luaCompletionWindow = new CompletionWindow(textArea);
            _luaCompletionWindow.CloseWhenCaretAtBeginning = true;
            _luaCompletionWindow.Closed += (_, _) => _luaCompletionWindow = null;

            var data = _luaCompletionWindow.CompletionList.CompletionData;
            foreach (var item in BuildLuaCompletionItems())
                data.Add(item);

            if (data.Count > 0)
                _luaCompletionWindow.Show();
            else
                _luaCompletionWindow = null;
        }

        private List<LuaCompletionItem> BuildLuaCompletionItems()
        {
            var items = new List<LuaCompletionItem>();

            foreach (string kw in LuaKeywords)
                items.Add(new LuaCompletionItem(kw, kw, "Lua关键字", "Keyword", 10));

            foreach (var meta in _luaApiMetaCache)
            {
                int priority = 5;
                items.Add(new LuaCompletionItem(meta.Name, meta.Signature, meta.Description, meta.Category, priority));
            }

            return items;
        }

        private static string GetCurrentWord(TextArea textArea)
        {
            int caretOffset = textArea.Caret.Offset;
            TextDocument document = textArea.Document;
            if (document == null || caretOffset <= 0)
                return string.Empty;

            int start = caretOffset;
            while (start > 0)
            {
                char c = document.GetCharAt(start - 1);
                if (!char.IsLetterOrDigit(c) && c != '_')
                    break;
                start--;
            }

            if (start >= caretOffset)
                return string.Empty;

            return document.GetText(start, caretOffset - start);
        }

        private sealed class LuaCompletionItem : ICompletionData
        {
            public string Text { get; }
            public object Content { get; }
            public object Description { get; }
            public IImage? Image => null;
            public double Priority { get; }

            private readonly string _insertText;

            public LuaCompletionItem(string name, string signature, string description, string category, double priority)
            {
                Text = name;
                _insertText = name;
                Priority = priority;
                Description = $"{signature}\n{category}\n{description}";

                var stack = new StackPanel { Orientation = Orientation.Horizontal };
                var nameBlock = new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.White
                };
                var sigBlock = new TextBlock
                {
                    Text = "  " + signature,
                    Foreground = Brushes.Gray,
                    FontSize = 12
                };
                stack.Children.Add(nameBlock);
                stack.Children.Add(sigBlock);
                Content = stack;
            }

            public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
            {
                textArea.Document.Replace(completionSegment, _insertText);
            }
        }

        // ========================================
        // Lua Diagnostics (Error Squiggly Lines)
        // ========================================

        private sealed class LuaDiagnosticsState
        {
            public TextEditor Editor = null!;
            public SquiggleRenderer Renderer = null!;
            public System.Timers.Timer? DebounceTimer;
            public string LastCheckedText = string.Empty;
        }

        private readonly Dictionary<TextEditor, LuaDiagnosticsState> _luaDiagnosticsStates = new();

        private void InitLuaDiagnostics(TextEditor editor)
        {
            var state = new LuaDiagnosticsState
            {
                Editor = editor,
                Renderer = new SquiggleRenderer()
            };

            editor.TextArea.TextView.BackgroundRenderers.Add(state.Renderer);

            state.DebounceTimer = new System.Timers.Timer(400);
            state.DebounceTimer.AutoReset = false;
            state.DebounceTimer.Elapsed += (_, _) =>
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => CheckLuaSyntaxForEditor(editor));
            };

            editor.TextChanged += (_, _) =>
            {
                if (_luaDiagnosticsStates.TryGetValue(editor, out var s))
                {
                    s.DebounceTimer?.Stop();
                    s.DebounceTimer?.Start();
                }
            };

            _luaDiagnosticsStates[editor] = state;
            CheckLuaSyntaxForEditor(editor);
        }

        private void CheckLuaSyntaxForEditor(TextEditor editor)
        {
            if (!_luaDiagnosticsStates.TryGetValue(editor, out var state))
                return;

            string text = editor.Text;
            if (text == state.LastCheckedText)
                return;

            state.LastCheckedText = text;

            var errors = EditorHostBridge.CheckLuaSyntax(text);
            state.Renderer.SetErrors(errors);
            editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        private sealed class SquiggleRenderer : IBackgroundRenderer
        {
            public KnownLayer Layer => KnownLayer.Selection;

            private readonly List<ErrorMarker> _errors = new();

            public void SetErrors(EditorHostBridge.LuaSyntaxError[] errors)
            {
                _errors.Clear();
                foreach (var e in errors)
                {
                    _errors.Add(new ErrorMarker
                    {
                        Line = e.Line,
                        Column = e.Column,
                        Message = e.Message
                    });
                }
            }

            public void Draw(TextView textView, DrawingContext drawingContext)
            {
                if (_errors.Count == 0)
                    return;

                var document = textView.Document;
                if (document == null)
                    return;

                var pen = new Pen(Brushes.Red, 1.2, null, PenLineCap.Round, PenLineJoin.Round);

                foreach (var error in _errors)
                {
                    var line = document.GetLineByNumber(Math.Max(1, error.Line));
                    if (line == null)
                        continue;

                    int startOffset = line.Offset + Math.Min(error.Column - 1, line.Length - 1);
                    int endOffset = startOffset;

                    char c = document.GetCharAt(startOffset);
                    while (endOffset + 1 < line.EndOffset && IsWordChar(document.GetCharAt(endOffset + 1)))
                        endOffset++;

                    if (!IsWordChar(c) && startOffset < line.EndOffset - 1)
                    {
                        startOffset++;
                        endOffset = startOffset;
                        while (endOffset + 1 < line.EndOffset && IsWordChar(document.GetCharAt(endOffset + 1)))
                            endOffset++;
                    }

                    if (endOffset < startOffset)
                        endOffset = startOffset;

                    if (endOffset - startOffset < 1)
                    {
                        if (endOffset + 1 < line.EndOffset)
                            endOffset++;
                        else if (startOffset > line.Offset)
                            startOffset--;
                    }

                    var segment = new SimpleSegment(startOffset, Math.Max(1, endOffset - startOffset + 1));
                    var builder = new BackgroundGeometryBuilder
                    {
                        CornerRadius = 0,
                        AlignToWholePixels = true
                    };
                    builder.AddSegment(textView, segment);

                    var geometry = builder.CreateGeometry();
                    if (geometry != null)
                    {
                        double offsetY = 1.5;
                        double amplitude = 1.5;
                        double period = 3.0;

                        foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        {
                            double y = rect.Bottom + offsetY;
                            double startX = rect.Left;
                            double endX = rect.Right;

                            var streamGeometry = new StreamGeometry();
                            using (var ctx = streamGeometry.Open())
                            {
                                ctx.BeginFigure(new Point(startX, y), false);
                                double x = startX;
                                bool up = true;
                                while (x < endX)
                                {
                                    double nextX = Math.Min(x + period / 2.0, endX);
                                    double waveY = y + (up ? amplitude : -amplitude);
                                    ctx.LineTo(new Point(nextX, waveY));
                                    up = !up;
                                    x = nextX;
                                }
                            }
                            drawingContext.DrawGeometry(null, pen, streamGeometry);
                        }
                    }
                }
            }

            private static bool IsWordChar(char c)
            {
                return char.IsLetterOrDigit(c) || c == '_';
            }

            private sealed class ErrorMarker
            {
                public int Line;
                public int Column;
                public string Message = string.Empty;
            }
        }
    }
}
