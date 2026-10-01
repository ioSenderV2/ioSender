/*
 * BulkObservableCollection.cs - part of CNC Core
 *
 * A plain ObservableCollection<T> fires one CollectionChanged notification PER item added - fine for a
 * user editing a handful of rows, ruinous for loading/restoring a program with hundreds of thousands of
 * lines into a collection a live, virtualizing DataGrid is bound to (confirmed on real hardware: a 220k-
 * line file, and separately GCode.File.Pop() restoring one, both froze the app - "Not Responding" - doing
 * exactly this). AddRange bypasses per-item notifications entirely (writes straight to the protected Items
 * list) and fires a single Reset at the end, so the UI thread does ONE layout pass instead of hundreds of
 * thousands of incremental ones.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace CNC.Core
{
    public class BulkObservableCollection<T> : ObservableCollection<T>
    {
        private bool suppressNotification = false;

        public void AddRange(IEnumerable<T> items)
        {
            if (items == null)
                return;

            suppressNotification = true;
            try
            {
                foreach (var item in items)
                    Items.Add(item);
            }
            finally
            {
                suppressNotification = false;
            }

            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <summary>
        /// Suppress change notifications for the duration of the returned scope, then raise ONE Reset.
        /// </summary>
        /// <remarks>
        /// For a builder that has to add items one at a time because each one is parsed as it arrives -
        /// AddRange and ReplaceAll both want the finished list up front, which such a builder does not have.
        ///
        /// The cost of not having this was measured on real hardware 2026-09-18: applying a height map to a
        /// 43,000-block program rebuilt it through GCode.AddBlock, one change notification per block, into a
        /// live DataGrid-bound collection. It took THIRTY SECONDS, during which the program list showed
        /// nothing - so a transform that had worked looked exactly like one that had silently failed.
        /// </remarks>
        public IDisposable DeferNotifications()
        {
            return new DeferScope(this);
        }

        private sealed class DeferScope : IDisposable
        {
            private readonly BulkObservableCollection<T> owner;
            private readonly bool wasSuppressed;

            public DeferScope(BulkObservableCollection<T> owner)
            {
                this.owner = owner;
                wasSuppressed = owner.suppressNotification;
                owner.suppressNotification = true;
            }

            public void Dispose()
            {
                if (wasSuppressed)
                    return;     // an outer scope owns the notification

                owner.suppressNotification = false;
                owner.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
                owner.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
                owner.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }

        // ReplaceAll: Clear (also suppressed - Clear() would otherwise fire its own Reset immediately)
        // followed by AddRange, as ONE Reset instead of two.
        public void ReplaceAll(IEnumerable<T> items)
        {
            suppressNotification = true;
            try
            {
                Items.Clear();
            }
            finally
            {
                suppressNotification = false;
            }
            AddRange(items);
        }

        protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
        {
            if (!suppressNotification)
                base.OnCollectionChanged(e);
        }
    }
}
