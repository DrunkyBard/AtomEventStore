﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Ploeh.AutoFixture.Xunit;
using Xunit.Extensions;
using Grean.AtomEventStore;
using Xunit;
using Moq;
using System.Xml;
using System.IO;

namespace Grean.AtomEventStore.UnitTests
{
    public class AtomEventStreamTests
    {
        [Theory, AutoAtomData]
        public void IdIsCorrect(
            [Frozen]UuidIri expected,
            AtomEventStream<TestEventX> sut)
        {
            UuidIri actual = sut.Id;
            Assert.Equal(expected, actual);
        }

        [Theory, AutoAtomData]
        public void StorageIsCorrect(
            [Frozen]IAtomEventStorage expected,
            AtomEventStream<TestEventX> sut)
        {
            IAtomEventStorage actual = sut.Storage;
            Assert.Equal(expected, actual);
        }

        [Theory, AutoAtomData]
        public void PageSizeIsCorrect(
            [Frozen]int expected,
            AtomEventStream<TestEventX> sut)
        {
            int actual = sut.PageSize;
            Assert.Equal(expected, actual);
        }

        [Theory, AutoAtomData]
        public void CreateSelfLinkFromReturnsCorrectResult(
            UuidIri id)
        {
            AtomLink actual = AtomEventStream.CreateSelfLinkFrom(id);

            var expected = AtomLink.CreateSelfLink(
                new Uri(
                    ((Guid)id).ToString(),
                    UriKind.Relative));
            Assert.Equal(expected, actual);
        }

        [Theory, AutoAtomData]
        public void AppendAsyncCorrectlyStoresFeedAndEntry(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory storage,
            AtomEventStream<TestEventX> sut,
            TestEventX expectedEvent)
        {
            // Fixture setup
            var before = DateTimeOffset.Now;

            // Exercise system
            sut.AppendAsync(expectedEvent).Wait();

            // Verify outcome
            var writtenFeed = storage.Feeds.Select(AtomFeed.Parse).Single();
            var expectedFeed = new AtomFeedLikeness(before, sut.Id, expectedEvent);
            Assert.True(
                expectedFeed.Equals(writtenFeed),
                "Expected feed must match actual feed.");

            var writtenEntry = storage.Entries.Select(AtomEntry.Parse).Single();
            var expectedEntry = new AtomEntryLikeness(
                before,
                expectedEvent,
                "self");
            Assert.True(
                expectedEntry.Equals(writtenEntry),
                "Expected entry must match actual entry.");

            // Teardown
        }

        [Theory, AutoAtomData]
        public void AppendAsyncCorrectlyStoresFeedAndEntries(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory storage,
            AtomEventStream<TestEventX> sut,
            TestEventX event1,
            TestEventX event2)
        {
            // Fixture setup
            var before = DateTimeOffset.Now;

            // Exercise system
            sut.AppendAsync(event1).Wait();
            sut.AppendAsync(event2).Wait();

            // Verify outcome
            var writtenFeed = storage.Feeds.Select(AtomFeed.Parse).Single();
            var writtenEntries = storage.Entries.Select(AtomEntry.Parse);

            var expectedFeed = new AtomFeedLikeness(before, sut.Id, event2);
            var expectedEntries = new HashSet<object>(
                new[]
                {
                    new AtomEntryLikeness(before, event1, "self"),
                    new AtomEntryLikeness(before, event2, "self")
                },
                new HashFreeEqualityComparer<object>());

            Assert.True(expectedFeed.Equals(writtenFeed));
            Assert.True(expectedEntries.SetEquals(writtenEntries));
            // Teardown
        }

        [Theory, AutoAtomData]
        public void AppendAsyncCorrectlyLinksSecondChangesetToFirst(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory storage,
            AtomEventStream<TestEventX> sut,
            TestEventX event1,
            TestEventX event2)
        {
            // Fixture setup

            // Exercise system
            sut.AppendAsync(event1).Wait();
            sut.AppendAsync(event2).Wait();

            // Verify outcome
            var writtenFeed = storage.Feeds.Select(AtomFeed.Parse).Single();
            var writtenEntries = storage.Entries.Select(AtomEntry.Parse);

            var headLink = writtenFeed
                .Entries.First()
                .Links.FirstOrDefault(l => l.IsViaLink);
            Assert.NotNull(headLink);
            var head = writtenEntries.Single(
                e => e.Links.Any(l => headLink.ToSelfLink().Equals(l)));
            var previousLink = head.Links.SingleOrDefault(l => l.Rel == "previous");
            Assert.NotNull(previousLink);
            var previous = writtenEntries.Single(
                e => e.Links.Any(l => previousLink.ToSelfLink().Equals(l)));
            Assert.False(
                previous.Links.Any(l => l.Rel == "previous"),
                "First entry can't have a previous link.");
            // Teardown
        }

        private class AtomFeedLikeness
        {
            private readonly DateTimeOffset minimumTime;
            private readonly UuidIri expectedId;
            private readonly object[] expectedEvents;

            public AtomFeedLikeness(
                DateTimeOffset minimumTime,
                UuidIri expectedId,
                params object[] expectedEvents)
            {
                this.minimumTime = minimumTime;
                this.expectedId = expectedId;
                this.expectedEvents = expectedEvents;
            }

            public override bool Equals(object obj)
            {
                var actual = obj as AtomFeed;
                if (actual == null)
                    return base.Equals(obj);

                var expectedEntries = this.expectedEvents
                    .Select(e => new AtomEntryLikeness(this.minimumTime, e, "via"))
                    .Cast<object>();

                return object.Equals(this.expectedId, actual.Id)
                    && object.Equals("Index of event stream " + (Guid)this.expectedId, actual.Title)
                    && this.minimumTime <= actual.Updated
                    && actual.Updated <= DateTimeOffset.Now
                    && expectedEntries.SequenceEqual(actual.Entries)
                    && actual.Links.Contains(
                        AtomEventStream.CreateSelfLinkFrom(this.expectedId));
            }

            public override int GetHashCode()
            {
                return 0;
            }
        }

        private class AtomEntryLikeness
        {
            private readonly DateTimeOffset minimumTime;
            private readonly object expectedEvent;
            private readonly string idRel;

            public AtomEntryLikeness(
                DateTimeOffset minimumTime,
                object expectedEvent,
                string idRel)
            {
                this.minimumTime = minimumTime;
                this.expectedEvent = expectedEvent;
                this.idRel = idRel;
            }

            public override bool Equals(object obj)
            {
                var actual = obj as AtomEntry;
                if (actual == null)
                    return base.Equals(obj);

                return !object.Equals(default(UuidIri), actual.Id)
                    && object.Equals("Changeset " + (Guid)actual.Id, actual.Title)
                    && this.minimumTime <= actual.Published
                    && actual.Published <= DateTimeOffset.Now
                    && this.minimumTime <= actual.Updated
                    && actual.Updated <= DateTimeOffset.Now
                    && actual.Links.Contains(
                        AtomEventStream
                            .CreateSelfLinkFrom(actual.Id)
                            .WithRel(this.idRel))
                    && object.Equals(this.expectedEvent, actual.Content.Item);
            }

            public override int GetHashCode()
            {
                return 0;
            }
        }

        private class HashFreeEqualityComparer<T> : IEqualityComparer<T>
        {
            public bool Equals(T x, T y)
            {
                return object.Equals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return 0;
            }
        }

        [Theory, AutoAtomData]
        public void SutIsEnumerable(AtomEventStream<TestEventY> sut)
        {
            Assert.IsAssignableFrom<IEnumerable<TestEventY>>(sut);
        }

        [Theory, AutoAtomData]
        public void SutIsInitiallyEmpty(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory dummyInjectedIntoSut,
            AtomEventStream<TestEventX> sut)
        {
            Assert.False(sut.Any(), "Intial event stream should be empty.");
            Assert.Empty(sut);
        }

        [Theory, AutoAtomData]
        public void SutYieldsCorrectEvents(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory dummyInjectedIntoSut,
            AtomEventStream<TestEventX> sut,
            List<TestEventX> events)
        {
            events.ForEach(e => sut.AppendAsync(e).Wait());

            var expected = events.AsEnumerable().Reverse();
            Assert.True(
                expected.SequenceEqual(sut),
                "Events should be yielded in a FILO order");
            Assert.True(
                expected.Cast<object>().SequenceEqual(sut.OfType<object>()),
                "Events should be yielded in a FILO order");
        }

        [Theory, AutoAtomData]
        public void SutCanAppendAndYieldPolymorphicEvents(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory dummyInjectedIntoSut,
            AtomEventStream<ITestEvent> sut,
            TestEventX tex,
            TestEventY tey)
        {
            sut.AppendAsync(tex).Wait();
            sut.AppendAsync(tey).Wait();

            var expected = new ITestEvent[] { tey, tex };
            Assert.True(expected.SequenceEqual(sut));
        }

        [Theory, AutoAtomData]
        public void SutCanAppendAndYieldEnclosedPolymorphicEvents(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory dummyInjectedIntoSut,
            AtomEventStream<Envelope<ITestEvent>> sut,
            Envelope<TestEventX> texEnvelope,
            Envelope<TestEventY> teyEnvelope)
        {
            var texA = texEnvelope.Cast<ITestEvent>();
            var teyA = teyEnvelope.Cast<ITestEvent>();

            sut.AppendAsync(texA).Wait();
            sut.AppendAsync(teyA).Wait();

            var expected = new Envelope<ITestEvent>[] { teyA, texA };
            Assert.True(expected.SequenceEqual(sut));
        }

        [Theory, AutoAtomData]
        public void SutIsObserver(AtomEventStream<TestEventX> sut)
        {
            Assert.IsAssignableFrom<IObserver<TestEventX>>(sut);
        }

        [Theory, AutoAtomData]
        public void OnNextAppendsItem(
            [Frozen(As = typeof(IAtomEventStorage))]AtomEventsInMemory dummyInjectedIntoSut,
            AtomEventStream<TestEventX> sut,
            TestEventX tex)
        {
            sut.OnNext(tex);
            Assert.Equal(tex, sut.SingleOrDefault());
        }

        [Theory, AutoAtomData]
        public void OnErrorDoesNotThrow(
            AtomEventStream<TestEventY> sut,
            Exception e)
        {
            Assert.DoesNotThrow(() => sut.OnError(e));
        }

        [Theory, AutoAtomData]
        public void OnCompletedDoesNotThrow(AtomEventStream<TestEventY> sut)
        {
            Assert.DoesNotThrow(() => sut.OnCompleted());
        }

        [Theory, AutoAtomData]
        public void AppendAsyncWritesEntryBeforeFeed(
            [Frozen]Mock<IAtomEventStorage> storageMock,
            AtomEventStream<TestEventX> sut,
            TestEventX tex,
            AtomFeed initialFeed)
        {
            // Fixture setup
            var stream = new MemoryStream();
            using (var xw = XmlWriter.Create(stream))
                initialFeed.WriteTo(xw);
            stream.Position = 0;
            storageMock
                .Setup(s => s.CreateFeedReaderFor(It.IsAny<Uri>()))
                .Returns(XmlReader.Create(stream));

            var actual = new List<int>();
            storageMock
                .Setup(s => s.CreateEntryWriterFor(It.IsAny<AtomEntry>()))
                .Callback(() => actual.Add(1))
                .Returns(XmlWriter.Create(new StringBuilder()));
            storageMock
                .Setup(s => s.CreateFeedWriterFor(It.IsAny<AtomFeed>()))
                .Callback(() => actual.Add(2))
                .Returns(XmlWriter.Create(new StringBuilder()));

            // Exercise system
            sut.AppendAsync(tex).Wait();

            // Verify outcome
            var expected = Enumerable.Range(1, 2);
            Assert.Equal(expected, actual);
            // Teardown
        }
    }
}
