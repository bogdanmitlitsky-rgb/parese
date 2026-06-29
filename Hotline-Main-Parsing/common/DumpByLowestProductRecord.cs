using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Hotline_Main_Parsing.common
{
    public sealed class DumpByLowestProductRecord : INotifyPropertyChanged
    {
        private bool _selected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value)
                {
                    return;
                }

                _selected = value;
                OnPropertyChanged();
            }
        }

        public string Section { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public DateTime LastSeenUtc { get; set; }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
