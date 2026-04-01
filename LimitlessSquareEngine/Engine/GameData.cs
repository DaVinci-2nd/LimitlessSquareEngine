using MoonSharp.Interpreter;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Engine
{
    [MoonSharpUserData]
    internal class GameData
    {
        private ConcurrentDictionary<string, object> _data = new ConcurrentDictionary<string, object>();
        public object this[string key]
        {
            get => _data.TryGetValue(key, out var value) ? value : null;
            set => _data[key] = value;
        }
    }
}
