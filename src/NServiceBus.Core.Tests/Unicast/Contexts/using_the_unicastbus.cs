namespace NServiceBus.Unicast.Tests.Contexts
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using Helpers;
    using Impersonation;
    using Impersonation.Windows;
    using MessageInterfaces.MessageMapper.Reflection;
    using MessageMutator;
    using Monitoring;
    using NUnit.Framework;
    using Publishing;
    using Queuing;
    using Rhino.Mocks;
    using Serializers.XML;
    using Subscriptions;
    using Subscriptions.SubcriberSideFiltering;
    using Timeout;
    using Unicast.Messages;
    using UnitOfWork;

    public class using_a_configured_unicastbus
    {
        protected IBus bus;

        protected UnicastBus unicastBus;
        protected ISendMessages messageSender;
        protected FakeSubscriptionStorage subscriptionStorage;

        protected Address gatewayAddress;
        MessageHeaderManager headerManager = new MessageHeaderManager();
        MessageMapper MessageMapper = new MessageMapper();

        protected FakeTransport Transport;
        protected XmlMessageSerializer MessageSerializer;
        protected FuncBuilder FuncBuilder;
        protected Address MasterNodeAddress;
        protected EstimatedTimeToSLABreachCalculator SLABreachCalculator = new EstimatedTimeToSLABreachCalculator();
        protected DefaultMessageRegistry messageRegistry;
        protected MessageDrivenSubscriptionManager subscriptionManager;
        SubscriptionPredicatesEvaluator subscriptionPredicatesEvaluator;

        [SetUp]
        public void SetUp()
        {
            Transport = new FakeTransport();
            FuncBuilder = new FuncBuilder();
            Configure.GetEndpointNameAction = () => "TestEndpoint";
            const string localAddress = "endpointA";
            MasterNodeAddress = new Address(localAddress, "MasterNode");
            subscriptionPredicatesEvaluator = new SubscriptionPredicatesEvaluator();

            messageRegistry = new DefaultMessageRegistry
                {
                    DefaultToNonPersistentMessages = Endpoint.IsVolatile
                };

            try
            {
                Address.InitializeLocalAddress(localAddress);
            }
            catch // intentional
            {
            }

            MessageSerializer = new XmlMessageSerializer(MessageMapper);
            ExtensionMethods.GetStaticOutgoingHeadersAction = () => MessageHeaderManager.staticHeaders;
            gatewayAddress = MasterNodeAddress.SubScope("gateway");

            messageSender = MockRepository.GenerateStub<ISendMessages>();
            subscriptionStorage = new FakeSubscriptionStorage();

            subscriptionManager = new MessageDrivenSubscriptionManager
                {
                    Builder = FuncBuilder,
                    MessageSender = messageSender,
                    SubscriptionStorage = subscriptionStorage
                };

            FuncBuilder.Register<IMutateOutgoingTransportMessages>(() => headerManager);
            FuncBuilder.Register<IMutateIncomingMessages>(() => new FilteringMutator
                {
                    SubscriptionPredicatesEvaluator = subscriptionPredicatesEvaluator
                });
            FuncBuilder.Register<IMutateOutgoingTransportMessages>(() => new SentTimeMutator());
            FuncBuilder.Register<IMutateIncomingTransportMessages>(() => subscriptionManager);
            FuncBuilder.Register<DefaultDispatcherFactory>(() => new DefaultDispatcherFactory());
            FuncBuilder.Register<EstimatedTimeToSLABreachCalculator>(() => SLABreachCalculator);
            FuncBuilder.Register<IImpersonateClients>(() => new WindowsImpersonator());

            unicastBus = new UnicastBus
            {
                MasterNodeAddress = MasterNodeAddress,
                MessageSerializer = MessageSerializer,
                Builder = FuncBuilder,
                MessageSender = messageSender,
                Transport = Transport,
                AutoSubscribe = true,
                MessageMapper = MessageMapper,
                MessagePublisher = new StorageDrivenPublisher
                    {
                        MessageSender = messageSender,
                        SubscriptionStorage = subscriptionStorage
                    },
                MessageDeferrer = new TimeoutManagerBasedDeferral
                    {
                        MessageSender = messageSender,
                        TimeoutManagerAddress = MasterNodeAddress.SubScope("Timeouts")
                    },
                SubscriptionManager = subscriptionManager,
                MessageRegistry = messageRegistry,
                SubscriptionPredicatesEvaluator = subscriptionPredicatesEvaluator
            };
            bus = unicastBus;

            FuncBuilder.Register<IMutateOutgoingTransportMessages>(() => new CausationMutator { Bus = bus });
            FuncBuilder.Register<IBus>(() => bus);

            ExtensionMethods.SetHeaderAction = headerManager.SetHeader;
        }

        protected void VerifyThatMessageWasSentTo(Address destination)
        {
            messageSender.AssertWasCalled(x => x.Send(Arg<TransportMessage>.Is.Anything, Arg<Address>.Is.Equal(destination)));
        }

        protected void VerifyThatMessageWasSentWithHeaders(Func<IDictionary<string, string>, bool> predicate)
        {
            messageSender.AssertWasCalled(x => x.Send(Arg<TransportMessage>.Matches(t => predicate(t.Headers)), Arg<Address>.Is.Anything));
        }

        protected void RegisterUow(IManageUnitsOfWork uow)
        {
            FuncBuilder.Register<IManageUnitsOfWork>(() => uow);
        }

        protected void RegisterMessageHandlerType<T>() where T : new()
        {
            FuncBuilder.Register<T>(() => new T());
            unicastBus.MessageHandlerTypes = new[] { typeof(T) };

            if (unicastBus.MessageDispatcherMappings == null)
                unicastBus.MessageDispatcherMappings = new Dictionary<Type, Type>();

            unicastBus.MessageDispatcherMappings[typeof(T)] = typeof(DefaultDispatcherFactory);
        }
        protected void RegisterOwnedMessageType<T>()
        {
            unicastBus.MessageOwners = new Dictionary<Type, Address> { { typeof(T), Address.Local } };
        }
        protected Address RegisterMessageType<T>()
        {
            var address = new Address(typeof(T).Name, "localhost");
            RegisterMessageType<T>(address);

            return address;
        }

        protected void RegisterMessageType<T>(Address address)
        {
            MessageMapper.Initialize(new[] { typeof(T) });
            MessageSerializer.Initialize(new[] { typeof(T) });
            unicastBus.RegisterMessageType(typeof(T), address);
            messageRegistry.RegisterMessageType(typeof(T));

        }

        protected void StartBus()
        {
            bus.Start();
        }

        protected void AssertSubscription(Predicate<TransportMessage> condition, Address addressOfPublishingEndpoint)
        {
            try
            {
                messageSender.AssertWasCalled(x =>
                  x.Send(Arg<TransportMessage>.Matches(m => condition(m)), Arg<Address>.Is.Equal(addressOfPublishingEndpoint)));

            }
            catch (Exception)
            {
                //retry to avoid race conditions 
                Thread.Sleep(2000);
                messageSender.AssertWasCalled(x =>
                 x.Send(Arg<TransportMessage>.Matches(m => condition(m)), Arg<Address>.Is.Equal(addressOfPublishingEndpoint)));
            }
        }
    }

    public class using_the_unicastbus : using_a_configured_unicastbus
    {
        [SetUp]
        public new void SetUp()
        {
            StartBus();
        }

        protected Exception ResultingException;

        protected void ReceiveMessage(TransportMessage transportMessage)
        {
            try
            {
                Transport.FakeMessageBeeingProcessed(transportMessage);
            }
            catch (Exception ex)
            {
                ResultingException = ex;
            }
        }

        protected void SimulateMessageBeeingAbortedDueToRetryCountExceeded(TransportMessage transportMessage)
        {
            try
            {
                Transport.FakeMessageBeeingPassedToTheFaultManager(transportMessage);
            }
            catch (Exception ex)
            {
                ResultingException = ex;
            }
        }


    }
}
