// ****************************************************************************
///*!	\file SortedObservableCollection.cs
// *	\brief An ObservableCollection with a Sortable Func
// *
// *	\copyright	Copyright 2012-2017 FlexRadio Systems.  All Rights Reserved.
// *				Unauthorized use, duplication or distribution of this software is
// *				strictly prohibited by law.
// *
// *	\date 2015-10-07
// *	\author Eric Wachsmann, KE5DTO
// */
// ****************************************************************************

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;


namespace Flex.UiWpfFramework.Mvvm
{
    public class SortedObservableCollection<T> : ObservableCollection<T>
    {
        private readonly Func<T, int> func;

        public SortedObservableCollection(Func<T, int> func)
        {
            this.func = func;
        }

        public SortedObservableCollection(Func<T, int> func, IEnumerable<T> collection)
            : base(collection)
        {
            this.func = func;
        }

        public SortedObservableCollection(Func<T, int> func, List<T> list)
            : base(list)
        {
            this.func = func;
        }

        protected override void InsertItem(int index, T item)
        {
            bool added = false;
            for (int idx = 0; idx < Count; idx++)
            {
                if (func(item) < func(Items[idx]))
                {
                    base.InsertItem(idx, item);
                    added = true;
                    break;
                }
            }

            if (!added)
            {
                base.InsertItem(index, item);
            }
        }
    }
}
