using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Collections;
using Microsoft.Windows.EventTracing;

namespace Common
{
    public struct TemporalEvent
    {
        public Type Type;
        public Object Value;
        public Timestamp Timestamp;
    }

    public class ChronologicalEnumerator : IEnumerator<TemporalEvent>
    {
        private abstract class IDataSource
        {
            public Type Type;
            public bool Halt;

            public abstract Timestamp getTimestamp();

            public abstract IEnumerator getEnumerator();
        }

        private class DataSource<T> : IDataSource
        {
            public Func<T, Timestamp> Timestamp;
            public IEnumerator<T> enumerator;

            public DataSource(Type type, IEnumerator<T> e, Func<T, Timestamp> p, bool h)
            {
                Type = type;
                this.enumerator = e;
                this.Timestamp = p;
                this.Halt = h;
            }

            public override Timestamp getTimestamp()
            {
                if (enumerator != null && enumerator.Current != null)
                    return Timestamp(enumerator.Current);

                return new Timestamp(0);
            }


            public override IEnumerator getEnumerator() => enumerator;
        }

        private LinkedList<IDataSource> orderedDataSources;

        private LinkedList<IDataSource> completedDataSources;


        public ChronologicalEnumerator()
        {
            orderedDataSources = new LinkedList<IDataSource>();
            completedDataSources = new LinkedList<IDataSource>();
        }

        public void addCollection(IEnumerator<TemporalEvent> enumerator) { this.addCollection(enumerator, false); }

        public void addCollection(IEnumerator<TemporalEvent> enumerator, bool halt)
        {
            // Validate that the enumerators are provided in Chronological order
#if DEBUG
            Timestamp prev = new Timestamp(0);
            Timestamp cur = new Timestamp(0);

            while (enumerator.MoveNext())
            {
                cur = (enumerator.Current).Timestamp;
                if (prev > cur)
                {
                    Debugger.Break();
                }

                prev = cur;

            }

            enumerator.Reset();
#endif

            enumerator.MoveNext();
            insert((new DataSource<TemporalEvent>(typeof(TemporalEvent), enumerator, (TemporalEvent t) => t.Timestamp, halt)));
        }

        public void addCollection<T>(IEnumerator<T> enumerator, Func<T, Timestamp> p) { this.addCollection(enumerator, p, false);  }

        public void addCollection<T>(IEnumerator<T> enumerator, Func<T, Timestamp> p, bool halt)
        {
            // Validate that the enumerators are provided in Chronological order
#if DEBUG
            /*
            Timestamp prev = new Timestamp(0);
            Timestamp cur = new Timestamp(0);

            while (enumerator.MoveNext())
            {
                cur = p(enumerator.Current);
                if (prev > cur)
                {
                    Debugger.Break();
                }

                prev = cur;

            }

            enumerator.Reset();
            */
#endif

            enumerator.MoveNext();
            insert((new DataSource<T>(typeof(T), enumerator, p, halt)));
        }

        private void insert(IDataSource t)
        {
            if (orderedDataSources.Count == 0)
            {
                orderedDataSources.AddLast(t);
                return;
            }

            for (LinkedListNode<IDataSource> curNode = orderedDataSources.First; curNode != null; curNode = curNode.Next)
            {
                if (curNode.Value.getTimestamp() > t.getTimestamp())
                {
                    orderedDataSources.AddBefore(curNode, t);
                    return;
                }

                if (curNode.Next == null)
                {
                    orderedDataSources.AddLast(t);
                    return;
                }
            }
        }

        public bool MoveNext()
        {
            return MoveNext(null);
        }

        public bool MoveNext(Timestamp? stopTime)
        {
            bool success = false;
            do
            {
                IDataSource first = orderedDataSources.First.Value;
                success = first.getEnumerator().MoveNext();
                orderedDataSources.RemoveFirst();

                // If the enumerator has more elements, re-insert in proper sorted order
                if (success)
                {
                    insert(first);

                    // If the next available element will exceed our Stoptime, we are done
                    if (stopTime.HasValue && stopTime <= orderedDataSources.First.Value.getTimestamp())
                    {
                        return false;
                    }
                    break;
                }
                else
                {
                    // Insert finished enumerators into lists
                    completedDataSources.AddLast(first);

                    if (orderedDataSources.Count == 0)
                    {
                        return false;
                    }
                    else
                    {
                        // The particular enumerator has run out of events, check if this is a critical event source and we should stop now
                        return !first.Halt;
                    }
                    
                }

            } while (orderedDataSources.Count > 0 && !success);

            return success;
        }

        public bool MoveUntil(Timestamp destination)
        {
            bool success = false;
            while ((orderedDataSources.First?.Value.getTimestamp() ?? Timestamp.MaxValue) < destination)
            {
                success = MoveNext();

                if (!success)
                    break;
            }

            return success;
        }

        public void Reset()
        {
            List<IDataSource> dataSources = new List<IDataSource>();

            while (completedDataSources.Count > 0)
            {
                dataSources.Add(completedDataSources.First.Value);
                completedDataSources.RemoveFirst();
            }

            while (orderedDataSources.Count > 0)
            {
                dataSources.Add(orderedDataSources.First.Value);
                orderedDataSources.RemoveFirst();
            }


            foreach (IDataSource source in dataSources)
            {
                source.getEnumerator().Reset();
                source.getEnumerator().MoveNext();
                this.insert(source);
            }
        }

        void IDisposable.Dispose() { }

        public TemporalEvent Current
        {
            get
            {
                if (orderedDataSources.First.Value.Type == typeof(TemporalEvent))
                {
                    // This is a bit of an ugly hack. The multicore enumerator will have an enumeration of TemporalEvents. This abstractly wraps the underlying typed data.
                    // Rip out the extra layer of abstraction here.
                    TemporalEvent a = (TemporalEvent)(orderedDataSources.First.Value.getEnumerator().Current);

                    TemporalEvent t;
                    t.Type = a.Type;
                    t.Value = a.Value;
                    t.Timestamp = a.Timestamp;
                    return t;
                }
                else
                {
                    TemporalEvent t;
                    t.Type = orderedDataSources.First.Value.Type;
                    t.Value = orderedDataSources.First.Value.getEnumerator().Current;
                    t.Timestamp = orderedDataSources.First.Value.getTimestamp();
                    return t;
                }
            }
        }

        object IEnumerator.Current
        {
            get { return Current; }
        }

        public IEnumerator GetEnumerator()
        {
            return null;
        }
    }
}
