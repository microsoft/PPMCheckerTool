using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Windows.EventTracing;
using Microsoft.Windows.EventTracing.Tables;
using Microsoft.Windows.EventTracing.Metadata;
using Microsoft.Windows.EventTracing.Cpu;
using Microsoft.Windows.EventTracing.Power;
using Microsoft.Windows.EventTracing.WaitAnalysis;
using Microsoft.Windows.EventTracing.Utc;
using Microsoft.Windows.EventTracing.Symbols;
using System.Collections;
using System.Threading.Tasks;

namespace Common
{
    public class MulticoreChronologicalEnumerator : IEnumerator<TemporalEvent>
    {
        private abstract class IDataSource
        {
            public Type Type;

            public abstract bool MoveNext();
            public abstract void Reset();
            public abstract Timestamp getFirstReliableTimestamp();

            public abstract TemporalEvent Current();
        }

        private class DataSource<T> : IDataSource
        {
            public Func<T, Timestamp> Timestamp;
            public Func<T, uint> Processor;
            public IReadOnlyList<T> collection;
            private LinkedList<int> perCPUIndices;

            public DataSource(Type type, IReadOnlyList<T> collection, Func<T, Timestamp> timestampFunc, Func<T, uint> processorFunc)
            {
                Type = type;
                this.collection = collection;
                this.Timestamp = timestampFunc;
                this.Processor = processorFunc;

                this.Reset();
            }

            public override Timestamp getFirstReliableTimestamp()
            {
                Timestamp firstEvent = Microsoft.Windows.EventTracing.Timestamp.Zero;

                for (LinkedListNode<int> curNode = perCPUIndices.First; curNode != null; curNode = curNode.Next)
                {
                    if (Timestamp(collection[curNode.Value]) > firstEvent)
                    {
                        firstEvent = Timestamp(collection[curNode.Value]);
                    }
                }

                return firstEvent;
            }

            public override bool MoveNext()
            {
                int index = perCPUIndices.First.Value;

                bool success = (index + 1 < collection.Count) &&
                                collection[index + 1] != null &&
                                Processor(collection[index]) == Processor(collection[index + 1]);

                perCPUIndices.RemoveFirst();

                // If the enumerator has more elements, re-insert in proper sorted order
                if (success)
                {
                    insertSorted(index + 1);
                }

                return success;
            }

            public override void Reset()
            {
                perCPUIndices = new LinkedList<int>();
                uint curCore = 0;
                insertSorted(0);

                for (int i = 0; i < collection.Count; i++)
                {
                    if (Processor(collection[i]) > curCore)
                    {
                        curCore = Processor(collection[i]);
                        insertSorted(i);
                    }
                }
            }

            private void insertSorted(int newIndex)
            {
                if (perCPUIndices.Count == 0)
                {
                    perCPUIndices.AddLast(newIndex);
                    return;
                }

                for (LinkedListNode<int> curNode = perCPUIndices.First; curNode != null; curNode = curNode.Next)
                {
                    if (Timestamp(collection[curNode.Value]) > Timestamp(collection[newIndex]))
                    {
                        perCPUIndices.AddBefore(curNode, newIndex);
                        return;
                    }

                    if (curNode.Next == null)
                    {
                        perCPUIndices.AddLast(newIndex);
                        return;
                    }
                }
            }

            public override TemporalEvent Current()
            {
                TemporalEvent t;
                t.Type = this.Type;
                t.Value = collection[perCPUIndices.First.Value];
                t.Timestamp = Timestamp(collection[perCPUIndices.First.Value]);
                return t;
            }

        }

        private IDataSource collection;


        public static MulticoreChronologicalEnumerator Create<T>(IReadOnlyList<T> collection, Func<T, Timestamp> timestampFunc, Func<T, uint> processorFunc)
        {
            MulticoreChronologicalEnumerator e = new MulticoreChronologicalEnumerator();
            e.collection = new DataSource<T>(typeof(T), collection, timestampFunc, processorFunc);
            return e;
        }

        public Timestamp getFirstReliableTimestamp()
        {
            return this.collection.getFirstReliableTimestamp();
        }

        public bool MoveNext()
        {
            return this.collection.MoveNext();
        }

        public void Reset()
        {
            this.collection.Reset();
        }

        void IDisposable.Dispose() { }

        public TemporalEvent Current
        {
            get
            {
                return this.collection.Current();
            }
        }
        object IEnumerator.Current
        {
            get { return Current; }
        }

    }
}
