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
        private bool canAccept;
        private bool canReject;
        private long efomId;
        private string acceptBlock = "";
        private string rejectBlock = "";
        private int minutes = DefaultMinutes;
        private bool busy;

        // The minutes a new row offers (the shop's last choice, Settings.AcceptMinutes).
        public static int DefaultMinutes { get; set; } = 20;

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

        // Αποδοχή here flips the connector's row, so it exists only while that row waits.
        public bool CanAccept { get => canAccept; private set => Set(ref canAccept, value); }
        public long EfomId { get => efomId; private set => Set(ref efomId, value); }
        public string AcceptBlock { get => acceptBlock; private set => Set(ref acceptBlock, value); }
        public bool CanReject { get => canReject; private set => Set(ref canReject, value); }
        public string RejectBlock { get => rejectBlock; private set => Set(ref rejectBlock, value); }

        // What the minutes picker holds (two-way bound) and whether the buttons are shown.
        public int Minutes { get => minutes; set => Set(ref minutes, value); }
        public bool Busy { get => busy; set { if (Set(ref busy, value)) Raise(nameof(Deciding)); } }
        public bool Deciding => !Leaving && !Busy;

        public void Update(WaitingOrder w)
        {
            if (Leaving) return;
            StateText = w.StateText;
            Level = w.Level;
            Hint = w.Hint;
            EfomId = w.Row?.EfomId ?? 0;
            AcceptBlock = OrderMirror.CanAcceptHere(w) ?? "";
            CanAccept = AcceptBlock.Length == 0;
            RejectBlock = OrderMirror.CanRejectHere(w) ?? "";
            CanReject = RejectBlock.Length == 0;
        }

        // The till answered: say so, then the board lets the row go.
        public void Leave(MirrorEvent? verdict)
        {
            Leaving = true;
            Raise(nameof(Deciding));
            Hint = "";
            CanAccept = false;
            CanReject = false;
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
