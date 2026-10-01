using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CatalogBridge
{
    // One row of the «Online παραγγελίες» board, updated in place every few seconds so the
    // row keeps its identity on screen (no flicker, the enter motion plays once).
    // Level: 0 waiting at the till · 1 take a look · 2 stuck · 3 accepted · 4 rejected.
    public sealed class WaitingRow : INotifyPropertyChanged
    {
        public string Uuid { get; }
        public string Token { get; }
        public string Who { get; }
        public string Items { get; }

        private string stateText = "";
        private int level;
        private string hint = "";

        public WaitingRow(WaitingOrder w)
        {
            Uuid = w.Order.Uuid;
            Token = w.Order.Token;
            Who = $"{w.Order.Customer} · {w.Order.Total:0.00} € · {w.Order.DeliveryText}, {w.Order.WhenText}";
            Items = w.Order.Items + (w.Order.Comment.Length > 0 ? $" — «{w.Order.Comment}»" : "");
            Update(w);
        }

        public string StateText { get => stateText; private set => Set(ref stateText, value); }
        public int Level { get => level; private set => Set(ref level, value); }
        public string Hint { get => hint; private set { if (Set(ref hint, value)) Raise(nameof(HasHint)); } }
        public bool HasHint => Hint.Length > 0;
        public bool Leaving { get; private set; }

        public void Update(WaitingOrder w)
        {
            if (Leaving) return;
            StateText = w.StateText;
            Level = w.Level;
            Hint = w.Hint;
        }

        // The till answered: say so, then the board lets the row go.
        public void Leave(MirrorEvent? verdict)
        {
            Leaving = true;
            Hint = "";
            if (verdict == null) { StateText = "Έφυγε"; return; }
            bool accepted = verdict.Action == MirrorAction.Accept;
            StateText = accepted ? (verdict.Minutes > 0 ? $"Δεκτή · {verdict.Minutes}′" : "Δεκτή") : "Απορρίφθηκε";
            Level = accepted ? 3 : 4;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
