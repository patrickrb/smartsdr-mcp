// ****************************************************************************
///*!	\file SafeObservableCollection.cs
// *	\brief Threadsafe Collection friendly for WPF
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2012-03-05
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading;
using System.Windows.Threading;
using System.Collections;
using System.Collections.ObjectModel;
using System.Windows;
using System.Diagnostics;


namespace Flex.UiWpfFramework.Mvvm
{
    //http://deanchalk.com/2010/02/01/thread-safe-dispatcher-safe-observable-collection-for-wpf/
    public class SafeObservableCollection<T> : IList<T>, INotifyCollectionChanged
    {

        private IList<T> collection = new List<T>();
        private IList<T> _dependableCollection = new List<T>();

        private Dispatcher dispatcher;

        public event NotifyCollectionChangedEventHandler CollectionChanged;

        private ReaderWriterLock sync = new ReaderWriterLock();

        private void GetReadLock()
        {
            //Debug.WriteLine("Thread: " + Thread.CurrentThread.Name + " ReadLock()");
            sync.AcquireReaderLock(Timeout.Infinite);
        }

        private void ReleaseReadLock()
        {
            //Debug.WriteLine("Thread: " + Thread.CurrentThread.Name + " ReleaseReadLock()");
            sync.ReleaseReaderLock();
        }

        private void GetWriteLock()
        {
            //Debug.WriteLine("Thread: " + Thread.CurrentThread.Name + " WriteLock()");
            sync.AcquireWriterLock(Timeout.Infinite);
        }

        private void ReleaseWriteLock()
        {
            //Debug.WriteLine("Thread: " + Thread.CurrentThread.Name + " ReleaseWriteLock()");
            sync.ReleaseWriterLock();
        }

        public SafeObservableCollection()
        {
            // Ensure that the dispatcher is for the GUI thread
            if (Application.Current == null)
                dispatcher = null;
            else
                dispatcher = Application.Current.Dispatcher;
        }

        public SafeObservableCollection(ObservableCollection<T> sourceCollection)
        {
            // Ensure that the dispatcher is for the GUI thread.  
            // Application.Current will be null when unit testing
            if (Application.Current == null)
                dispatcher = null;
            else
                dispatcher = Application.Current.Dispatcher;

            T[] copiedArray = new T[sourceCollection.Count];
            sourceCollection.CopyTo(copiedArray, 0);

            foreach (var item in copiedArray)
            {
                this.Add(item);
            }
        }

        public SafeObservableCollection(SafeObservableCollection<T> sourceCollection)
        {
            // Ensure that the dispatcher is for the GUI thread.  
            // Application.Current will be null when unit testing
            if (Application.Current == null)
                dispatcher = null;
            else
                dispatcher = Application.Current.Dispatcher;

            T[] copiedArray = new T[sourceCollection.SafeCount];
            sourceCollection.SafeCopyTo(copiedArray, 0);

            foreach (var item in copiedArray)
            {
                this.Add(item);
            }
        }

        public SafeObservableCollection(IList<T> sourceCollection)
        {
            // Ensure that the dispatcher is for the GUI thread.  
            // Application.Current will be null when unit testing
            if (Application.Current == null)
                dispatcher = null;
            else
                dispatcher = Application.Current.Dispatcher;

            T[] copiedArray = new T[sourceCollection.Count];
            sourceCollection.CopyTo(copiedArray, 0);

            foreach (var item in copiedArray)
            {
                this.Add(item);
            }
        }

        public List<T> SafeGetList()
        {
            GetWriteLock();
            T[] copiedArray = new T[this.SafeCount];
            _dependableCollection.CopyTo(copiedArray, 0);
            ReleaseWriteLock();

            List<T> copiedCollection = new List<T>();

            foreach (var item in copiedArray)
            {
                copiedCollection.Add(item);
            }

            return copiedCollection;
        }

        public SafeObservableCollection<T> SafeGetCopy()
        {
            GetWriteLock();
            T[] copiedArray = new T[this.SafeCount];
            _dependableCollection.CopyTo(copiedArray, 0);
            ReleaseWriteLock();

            SafeObservableCollection<T> copiedCollection = new SafeObservableCollection<T>();

            foreach (var item in copiedArray)
            {
                copiedCollection.Add(item);
            }

            return copiedCollection;
        }

        public void Add(T item)
        {
           GetWriteLock();
            _dependableCollection.Add(item);
            ReleaseWriteLock();

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoAdd(item);
            }
            else
            {
                dispatcher.BeginInvoke(() => { DoAdd(item); });
            }
        }

        private void DoAdd(T item)
        {
            GetWriteLock();
            collection.Add(item);

            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item));

            ReleaseWriteLock();
        }

        public void Clear()
        {
           GetWriteLock();
            _dependableCollection.Clear();
            ReleaseWriteLock();

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoClear();
            }
            else
            {
                dispatcher.BeginInvoke(() => { DoClear(); });
            }
        }

        private void DoClear()
        {
            GetWriteLock();
            collection.Clear();

            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

            ReleaseWriteLock();
        }

        [Obsolete("This is unsafe, use SafeContains() instead")]
        public bool Contains(T item)
        {
            GetReadLock();
            var result = collection.Contains(item);
            ReleaseReadLock();
            return result;
        }

        public bool SafeContains(T item)
        {
            GetReadLock();
            var result = _dependableCollection.Contains(item);
            ReleaseReadLock();
            return result;
        }

        [Obsolete("This is unsafe, use SafeCopyTo() instead")]
        public void CopyTo(T[] array, int arrayIndex)
        {
           GetWriteLock();
            collection.CopyTo(array, arrayIndex);
            ReleaseWriteLock();
        }

        public void SafeCopyTo(T[] array, int arrayIndex)
        {
           GetWriteLock();
            _dependableCollection.CopyTo(array, arrayIndex);
            ReleaseWriteLock();
        }

        [Obsolete("This is unsafe, use SafeCount instead")]
        public int Count
        {
            get
            {
                GetReadLock();
                var result = collection.Count;
                ReleaseReadLock();
                return result;
            }
        }

        public int SafeCount
        {
            get
            {
                GetReadLock();
                var result = _dependableCollection.Count;
                ReleaseReadLock();
                return result;
            }
        }

        [Obsolete("This is unsafe, use SafeIsReadOnly instead")]
        public bool IsReadOnly
        {
            get
            {
                GetReadLock();
                var result = collection.IsReadOnly;
                ReleaseReadLock();
                return result;
            }
        }

        public bool SafeIsReadOnly
        {
            get
            {
                GetReadLock();
                var result = _dependableCollection.IsReadOnly;
                ReleaseReadLock();
                return result;
            }
        }


        // The DispatcherOperation in this funciton is likely not valid, since BeginInvoke is
        // asynchronous.  An event ahndler for op.Completed would be required to get the 
        // actual result.
        //public bool Remove(T item)
        //{
        //    if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
        //    {
        //        return DoRemove(item);
        //    }
        //    else
        //    {
        //        var op = dispatcher.BeginInvoke(new Func<T, bool>(DoRemove), item);
        //        if (op == null || op.Result == null)
        //        {
        //            return false;
        //        }
        //        return (bool)op.Result;
        //    }
        //}

        public bool Remove(T item)
        {
            var result = false;

           GetWriteLock();
            var index = _dependableCollection.IndexOf(item);

            if (index == -1)
            {
                ReleaseWriteLock();
                return false;
            }

            result = _dependableCollection.Remove(item);
            ReleaseWriteLock();           

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoRemove(item);
            }
            else
            {
                dispatcher.BeginInvoke(new Func<T, bool>(DoRemove), item);
            }

            return result;
        }

        private bool DoRemove(T item)
        {
           GetWriteLock();
            var index = collection.IndexOf(item);

            if (index == -1)
            {
                ReleaseWriteLock();
                return false;
            }

            var result = collection.Remove(item);

            if (result && CollectionChanged != null)
            {
                CollectionChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
            }
            ReleaseWriteLock();

            return result;
        }

        [Obsolete("This is unsafe, use SafeGetList() get access to Enumerator")]
        public IEnumerator<T> GetEnumerator()
        {
            IEnumerator<T> result = null;

            GetReadLock();

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                result = collection.GetEnumerator();
            }
            else
            {
                result = _dependableCollection.GetEnumerator();
            }

            ReleaseReadLock();

            return result;
        }


        [Obsolete("This is unsafe, use SafeGetCopy to iterate")]
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }


        [Obsolete("This is unsafe, use SafeIndexOf instead")]
        public int IndexOf(T item)
        {
            GetReadLock();
            var result = collection.IndexOf(item);
            ReleaseReadLock();
            return result;
        }

        public int SafeIndexOf(T item)
        {
            GetReadLock();
            var result = _dependableCollection.IndexOf(item);
            ReleaseReadLock();
            return result;
        }



        public void Insert(int index, T item)
        {
           GetWriteLock();
            _dependableCollection.Insert(index, item);
            ReleaseWriteLock();

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoInsert(index, item);
            }
            else
            {
                dispatcher.BeginInvoke(() => { DoInsert(index, item); });
            }
        }



        private void DoInsert(int index, T item)
        {
            GetWriteLock();
            collection.Insert(index, item);

            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));

            ReleaseWriteLock();
        }



        public void RemoveAt(int index)
        {
           GetWriteLock();

            if (_dependableCollection.Count == 0 || _dependableCollection.Count <= index)
            {
                ReleaseWriteLock();
                throw new ArgumentOutOfRangeException();
            }

            _dependableCollection.RemoveAt(index);
            ReleaseWriteLock();


            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoRemoveAt(index);
            }
            else
            {
                dispatcher.BeginInvoke(() => { DoRemoveAt(index); });
            }
        }



        private void DoRemoveAt(int index)
        {
           GetWriteLock();

            if (collection.Count == 0 || collection.Count <= index)
            {
                ReleaseWriteLock();
                throw new ArgumentOutOfRangeException();
            }

            var item = collection[index];
            collection.RemoveAt(index);

            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));

            ReleaseWriteLock();
        }

        public T SafeGetItemAtIndex(int index)
        {
            GetReadLock();
            if (_dependableCollection.Count == 0 || _dependableCollection.Count <= index)
            {
                ReleaseReadLock();
                throw new ArgumentOutOfRangeException();
            }
            var result = _dependableCollection[index];
            ReleaseReadLock();
            return result;
        }

        public void SafeSetItemAtIndex(int index, T item)
        {
            GetWriteLock();

            if (_dependableCollection.Count == 0 || _dependableCollection.Count <= index)
            {
                ReleaseWriteLock();
                throw new ArgumentOutOfRangeException();
            }

            _dependableCollection[index] = item;
            ReleaseWriteLock();

            if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
            {
                DoSet(index, item);
            }
            else
            {
                dispatcher.BeginInvoke(() => { DoSet(index, item); });
            }
        }

        [Obsolete("This is unsafe, use SafeGetItemAtIndex() and SafeSetItemAtIndex() instead")]
        public T this[int index]
        {
            get
            {
                if (dispatcher == null || Thread.CurrentThread == dispatcher.Thread)
                {
                    GetReadLock();
                    if (collection.Count == 0 || collection.Count <= index)
                    {
                        ReleaseReadLock();
                        throw new ArgumentOutOfRangeException();
                    }
                    var result = collection[index];
                    ReleaseReadLock();
                    return result;
                }
                else
                {
                    GetReadLock();
                    if (_dependableCollection.Count == 0 || _dependableCollection.Count <= index)
                    {
                        ReleaseReadLock();
                        throw new ArgumentOutOfRangeException();
                    }
                    var result = _dependableCollection[index];
                    ReleaseReadLock();
                    return result;
                }
            }
            set
            {
                SafeSetItemAtIndex(index, value);
            }
        }

        private void DoSet(int index, T item)
        {
           GetWriteLock();

            if (collection.Count == 0 || collection.Count <= index)
            {
                ReleaseWriteLock();
                throw new ArgumentOutOfRangeException();
            }

            var oldItem = collection[index];
            collection[index] = item;

            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Replace, item, oldItem, index));

            ReleaseWriteLock();
        }


        public bool IsSynced()
        {
            bool result = false;
            GetReadLock();
            if (collection.Count == _dependableCollection.Count)
                result = true;
            ReleaseReadLock();
            return result;
        }
    }
}
