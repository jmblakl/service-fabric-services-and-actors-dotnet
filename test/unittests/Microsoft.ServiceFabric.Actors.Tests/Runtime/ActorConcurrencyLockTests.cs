﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

namespace Microsoft.ServiceFabric.Actors.Tests.Runtime
{
    using System;
    using System.Fabric;
    using System.Numerics;
    using System.Threading.Tasks;
    using System.Threading;
    using Microsoft.ServiceFabric.Actors.Runtime;
    using Microsoft.ServiceFabric.Actors;
    using FluentAssertions;
    using Moq;
    using Xunit;
    
    interface IDummyActor : IActor
    {
        Task<string> Greetings();
    }
    
    public class DummyActor : Actor, IDummyActor
    {
        private static ActorService GetMockActorService()
        {
            var nodeContext = new NodeContext("MockNodeName", new NodeId(BigInteger.Zero, BigInteger.Zero), BigInteger.Zero,
                "MockNodeType", "0.0.0.0");

            var serviceContext = new StatefulServiceContext(
                nodeContext,
                new Mock<ICodePackageActivationContext>().Object,
                "MockServiceTypeName",
                new Uri("fabric:/MockServiceName"),
                null,
                Guid.Empty,
                long.MinValue);

            return new ActorService(serviceContext, 
                ActorTypeInformation.Get(typeof(DummyActor)));
        }
        
        public DummyActor() : base(GetMockActorService(), null)
        {
        }

        public Task<string> Greetings()
        {
            return Task.FromResult("Hello");
        }
    }

    public class ActorConcurrencyLockTests
    {
        private delegate Task<bool> DirtyCallback(Actor actor);

        private static string _currentContext = Guid.Empty.ToString();
        
        /// <summary>
        /// Verifies usage of ReentrancyGuard.
        /// </summary>
        [Fact]
        public void VerifyReentrants()
        {
            var a = new DummyActor();
            var guard = CreateAndInitializeReentrancyGuard(a, ActorReentrancyMode.LogicalCallContext);

            var tasks = new Task[1];
            for (int i = 0; i < 1; ++i)
            {
                tasks[i] = Task.Run(() =>
                {
                    RunTest(guard);
                }
            );

            }
            Task.WaitAll(tasks);
        }

        private static void RunTest(ActorConcurrencyLock guard)
        {
            var test = Guid.NewGuid().ToString();
            guard.Acquire(test, null, CancellationToken.None).Wait();
            guard.Test_CurrentCount.Should().Be(1);
            _currentContext = test;
            for (var i = 0; i < 10; i++)
            {
                var testContext = test + ":" + Guid.NewGuid().ToString();
                guard.Acquire(testContext, null, CancellationToken.None).Wait();
                testContext.Should().StartWith(_currentContext, "Call context Prefix Matching ");
                guard.ReleaseContext(testContext).Wait();
            }

            guard.Test_CurrentCount.Should().Be(1);
            guard.ReleaseContext(test).Wait();
        }

        [Fact]
        public void VerifyDirtyCallbacks()
        {
            var actor = new DummyActor();
            var guard = CreateAndInitializeReentrancyGuard(actor, ActorReentrancyMode.LogicalCallContext);
            actor.IsDirty = true;
            string callContext = Guid.NewGuid().ToString();
            var result = guard.Acquire(callContext, @base => ReplacementHandler(actor), CancellationToken.None);
            try
            {
                result.Wait();
                actor.IsDirty.Should().BeFalse("ReentrancyGuard IsDirty should be set to false");
            }
            finally 
            {
                guard.ReleaseContext(callContext).Wait();
            }
            RunTest(guard);
        }

        private static Task<bool> ReplacementHandler(ActorBase actor)
        {
            actor.IsDirty.Should().BeTrue("Expect actor to be in dirty state when handler invoked");
            actor.IsDirty = false;
            return Task.FromResult((true));
        }

        [Fact]
        public void VerifyInvalidContextRelease()
        {
            var actor = new DummyActor();
            var guard = CreateAndInitializeReentrancyGuard(actor, ActorReentrancyMode.LogicalCallContext);
            var context = Guid.NewGuid().ToString();
            guard.Acquire(context, null, CancellationToken.None).Wait();
            guard.Test_CurrentContext.Should().Be(context);
            guard.Test_CurrentCount.Should().Be(1);

            Action action = () => guard.ReleaseContext(Guid.NewGuid().ToString()).Wait();
            action.ShouldThrow<AggregateException>();

            guard.ReleaseContext(context).Wait();
            guard.Test_CurrentContext.Should().NotBe(context);
            guard.Test_CurrentCount.Should().Be(0);
        }

        [Fact]
        public void ReentrancyDisallowedTest()
        {
            var actor = new DummyActor();
            var guard = CreateAndInitializeReentrancyGuard(actor, ActorReentrancyMode.Disallowed);
            var context = Guid.NewGuid().ToString();
            guard.Acquire(context, null, CancellationToken.None).Wait();
            guard.Test_CurrentContext.Should().Be(context);
            guard.Test_CurrentCount.Should().Be(1);

            Action action = () => guard.Acquire(context, null, CancellationToken.None).Wait();
            action.ShouldThrow<AggregateException>();

            guard.ReleaseContext(context).Wait();
            guard.Test_CurrentContext.Should().NotBe(context);
            guard.Test_CurrentCount.Should().Be(0);
        }

        private ActorConcurrencyLock CreateAndInitializeReentrancyGuard(ActorBase owner, ActorReentrancyMode mode)
        {
            var settings = new ActorConcurrencySettings() { ReentrancyMode = mode };
            var guard = new ActorConcurrencyLock(owner, settings);
            return guard;
        }
    }
}
