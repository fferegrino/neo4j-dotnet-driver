﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Neo4j.Driver.Internal.result;
using Xunit;

namespace Neo4j.Driver.Tests.Result
{
    public class ResultBuilderTests
    {
        public class CollectMetaMethod
        {
            [Fact]
            public void ShouldCollectKeys()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                { {"fields", new List<object> {"fieldKey1", "fieldKey2", "fieldKey3"} } };
                builder.CollectMeta(meta);

                var result = builder.Build();
                result.Keys.Should().ContainInOrder("fieldKey1", "fieldKey2", "fieldKey3");
            }

            [Fact]
            public void ShouldCollectType()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                { {"type", "r" } };
                builder.CollectMeta(meta);

                var result = builder.Build();
                result.Summarize().StatementType.Should().Be(StatementType.ReadOnly);
            }

            [Fact]
            public void ShouldCollectStattistics()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                { {"stats", new Dictionary<string, object> { {"nodes-created", 10}, {"nodes-deleted", 5} } } };
                builder.CollectMeta(meta);

                var result = builder.Build();
                var statistics = result.Summarize().UpdateStatistics;
                statistics.NodesCreated.Should().Be(10);

            }

            [Fact]
            public void ShouldCollectNotifications()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                {
                    {
                        "notifications", new List<object>
                        {
                            new Dictionary<string, object> {{"code", "CODE"}, {"title", "TITLE"}},
                            new Dictionary<string, object>
                            {
                                {"description", "DES"},
                                {
                                    "position", new Dictionary<string, object>
                                    {
                                        {"offset", 11}
                                    }
                                }
                            }
                        }
                    }
                };

                builder.CollectMeta(meta);

                InputPosition position = new InputPosition(0,0,0);

                var result = builder.Build();
                var notifications = result.Summarize().Notifications;
                notifications.Should().HaveCount(2);
                notifications[0].Code.Should().Be("CODE");
                notifications[0].Title.Should().Be("TITLE");
                notifications[0].Position.Offset.Should().Be(0);
                notifications[0].Position.Column.Should().Be(0);
                notifications[0].Position.Line.Should().Be(0);
                notifications[0].Description.Should().BeEmpty();

                notifications[1].Description.Should().Be("DES");
                notifications[1].Code.Should().BeEmpty();
                notifications[1].Title.Should().BeEmpty();
                notifications[1].Position.Offset.Should().Be(11);
                notifications[1].Position.Column.Should().Be(0);
                notifications[1].Position.Line.Should().Be(0);
            }

            [Fact]
            public void ShouldCollectSimplePlan()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                { {"plan", new Dictionary<string, object>
                {
                    {"operatorType", "X"}
                } } };
                builder.CollectMeta(meta);

                var result = builder.Build();
                var plan = result.Summarize().Plan;
                plan.OperatorType.Should().Be("X");
                plan.Arguments.Should().BeEmpty();
                plan.Children.Should().BeEmpty();
                plan.Identifiers.Should().BeEmpty();

            }

            [Fact]
            public void ShouldCollectPlanThatContainsPlans()
            {
                var builder = new ResultBuilder();
                IDictionary<string, object> meta = new Dictionary<string, object>
                { {"plan", new Dictionary<string, object>
                {
                    {"operatorType", "X"},
                    { "args", new Dictionary<string, object> { {"a", 1}, {"b", "lala"} } },
                    { "identifiers", new List<object> {"id1", "id2"} },
                    { "children", new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            { "operatorType", "tt"},
                            { "children", new List<object>()}
                        },
                        new Dictionary<string, object>
                        {
                            { "children", new List<object>
                            {
                               new Dictionary<string, object> { { "operatorType", "Y"} }
                            } }
                        }
                        }
                    }
                } } };
                builder.CollectMeta(meta);

                var result = builder.Build();
                var plan = result.Summarize().Plan;
                plan.OperatorType.Should().Be("X");
                plan.Arguments.Should().ContainKey("a");
                plan.Arguments["a"].Should().Be(1);
                plan.Arguments.Should().ContainKey("b");
                plan.Arguments["b"].Should().Be("lala");
                plan.Identifiers.Should().ContainInOrder("id1", "id2");
                plan.Children.Should().NotBeNull();
                var children = plan.Children;
                children.Should().HaveCount(2);
                children[0].OperatorType.Should().Be("tt");
                children[0].Children.Should().BeNull();// TODO 
                children[1].Children.Should().HaveCount(1);
                children[1].Children[1].OperatorType.Should().Be("Y");
            }
        }
    }
}
