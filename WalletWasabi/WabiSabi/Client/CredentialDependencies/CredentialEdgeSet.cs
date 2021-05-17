using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace WalletWasabi.WabiSabi.Client.CredentialDependencies
{
	record CredentialEdgeSet
	{
		public CredentialType CredentialType { get; init; }
		public ImmutableDictionary<RequestNode, ImmutableHashSet<CredentialDependency>> Predecessors { get; init; } = ImmutableDictionary.Create<RequestNode, ImmutableHashSet<CredentialDependency>>();
		public ImmutableDictionary<RequestNode, ImmutableHashSet<CredentialDependency>> Successors { get; init; } = ImmutableDictionary.Create<RequestNode, ImmutableHashSet<CredentialDependency>>();
		public ImmutableDictionary<RequestNode, long> EdgeBalances { get; init; } = ImmutableDictionary.Create<RequestNode, long>();

		public long Balance(RequestNode node) => node.InitialBalance(CredentialType) + EdgeBalances[node];

		public ImmutableHashSet<CredentialDependency> InEdges(RequestNode node) => Predecessors[node];

		public ImmutableHashSet<CredentialDependency> OutEdges(RequestNode node) => Successors[node];

		public int InDegree(RequestNode node) => InEdges(node).Count();

		public int OutDegree(RequestNode node) => OutEdges(node).Count();

		public int RemainingInDegree(RequestNode node) => node.InitialBalance(CredentialType) <= 0 ? DependencyGraph.K - InDegree(node) : 0;

		public int RemainingOutDegree(RequestNode node) => node.InitialBalance(CredentialType) >= 0 ? DependencyGraph.K - OutDegree(node) : 0;

		public CredentialEdgeSet AddEdge(RequestNode from, RequestNode to, ulong value)
		{
			if (value == 0)
			{
				throw new ArgumentException("can't create edge with 0 value.");
			}

			var edge = new CredentialDependency(from, to, CredentialType, value);

			// Maintain subset of K-regular graph invariant
			if (RemainingOutDegree(edge.From) == 0 || RemainingInDegree(edge.To) == 0)
			{
				throw new InvalidOperationException("Can't add more than k edges.");
			}

			if (RemainingOutDegree(edge.From) == 1)
			{
				// This is the final out edge for the node edge.From
				if (Balance(edge.From) - (long)edge.Value > 0)
				{
					throw new InvalidOperationException("Can't add final out edge without discharging positive value.");
				}

				// If it's the final edge overall for that node, the final balance must be 0
				if (RemainingInDegree(edge.From) == 0 && Balance(edge.From) - (long)edge.Value != 0)
				{
					throw new InvalidOperationException("Can't add final edge without discharging negative value completely.");
				}
			}

			if (RemainingInDegree(edge.To) == 1)
			{
				// This is the final in edge for the node edge.To
				if (Balance(edge.To) + (long)edge.Value < 0)
				{
					throw new InvalidOperationException("Can't add final in edge without discharging negative value.");
				}

				// If it's the final edge overall for that node, the final balance must be 0
				if (RemainingOutDegree(edge.To) == 0 && Balance(edge.To) + (long)edge.Value != 0)
				{
					throw new InvalidOperationException("Can't add final edge without discharging negative value completely.");
				}
			}

			var predecessors = InEdges(edge.To);
			var successors = OutEdges(edge.From);

			return this with
			{
				Predecessors = Predecessors.SetItem(edge.To, predecessors.Add(edge)),
				Successors = Successors.SetItem(edge.From, successors.Add(edge)),
				EdgeBalances = EdgeBalances.SetItems(
					new KeyValuePair<RequestNode, long>[]
					{
						new (edge.From, EdgeBalances[edge.From] - (long)edge.Value),
						new (edge.To,   EdgeBalances[edge.To]   + (long)edge.Value),
					}),
			};
		}

		// Find the largest negative or positive balance node for the given
		// credential type, and one or more smaller nodes with a combined total
		// magnitude exceeding that of the largest magnitude node when possible.
		public bool SelectNodesToDischarge(
			IEnumerable<RequestNode> nodes,
			[NotNullWhen(true)] out RequestNode? largestMagnitudeNode,
			[NotNullWhen(true)] out IEnumerable<RequestNode> smallMagnitudeNodes,
			out bool fanIn)
		{
			// Order the given of the graph based on their balances
			var ordered = nodes.OrderByDescending(v => Balance(v));
			var positiveRequestNodes = ordered.ThenBy(x => OutDegree(x)).Where(v => Balance(v) > 0);
			var negativeRequestNodes = ordered.ThenByDescending(x => InDegree(x)).Where(v => Balance(v) < 0).Reverse();

			if (!negativeRequestNodes.Any())
			{
				largestMagnitudeNode = null;
				smallMagnitudeNodes = Array.Empty<RequestNode>();
				fanIn = false;
				return false;
			}

			int BalanceSign(int possitiveCount, int negativeCount) =>
				positiveRequestNodes.Take(possitiveCount).Sum(x => Balance(x)).CompareTo(
				negativeRequestNodes.Take(negativeCount).Sum(x => -Balance(x)));

			// We want to fully discharge the larger (in absolute magnitude) of
			// the two nodes, so we will add more nodes to the smaller one until
			// we can fully cover. At each step of the iteration we fully
			// discharge at least 2 nodes from the queue.
			(int, int, bool) EvaluateCombination(int prevSign, int p, int n, int availablePossitives, int availableNegatives)
			{
				var sign = BalanceSign(p, n);
				return (sign == prevSign, sign, availablePossitives, availableNegatives) switch
				{
					(true, < 0, > 0, _) => EvaluateCombination(sign, ++p, n, --availablePossitives, availableNegatives),
					(true, > 0, _, > 0) => EvaluateCombination(sign, p, ++n, availablePossitives, --availableNegatives),
					_ => (p, n, prevSign < 0)
				};
			}
			var initialSign = BalanceSign(1, 1);
			var (p, n, isFanIn) = EvaluateCombination(initialSign, 1, 1, positiveRequestNodes.Count(), negativeRequestNodes.Count());

			(largestMagnitudeNode, smallMagnitudeNodes) = isFanIn
				? (negativeRequestNodes.First(), positiveRequestNodes.Take(p).Reverse())
				: (positiveRequestNodes.First(), negativeRequestNodes.Take(n));

			fanIn = isFanIn;
			return true;
		}

		// Drain values into a reissuance request (towards the center of the graph).
		public CredentialEdgeSet DrainReissuance(RequestNode reissuance, IEnumerable<RequestNode> nodes)
			// The amount for the edge is always determined by the dicharged
			// nodes' values, since we only add reissuance nodes to reduce the
			// number of charged nodes overall.
			=> nodes.Aggregate(this, (edgeSet, node) => edgeSet.DrainReissuance(reissuance, node));

		// Drain credential values between terminal nodes, cancelling out
		// opposite values by propagating forwards or backwards corresponding to
		// fan-in and fan-out dependency structure.
		public CredentialEdgeSet DrainTerminal(RequestNode node, IEnumerable<RequestNode> nodes)
			=> nodes.Aggregate(this, (edgeSet, otherNode) => edgeSet.DrainTerminal(node, otherNode));

		private CredentialEdgeSet DrainReissuance(RequestNode reissuance, RequestNode node) =>
			// Due to opportunistic draining of lower priority credential
			// types when defining a reissuance node for higher priority
			// ones, the amount is not guaranteed to be zero, avoid adding
			// such edges.
			Balance(node) switch
			{
				> 0 and long v => AddEdge(node, reissuance, (ulong)v),
				< 0 and long v => AddEdge(reissuance, node, (ulong)(-1 * v)),
				_  => this
			};

		private CredentialEdgeSet DrainTerminal(RequestNode node, RequestNode dischargeNode) =>
			Balance(dischargeNode) switch
			{
				> 0 and long v => AddEdge(dischargeNode, node, (ulong)Math.Min(-1 * Balance(node), v)),
				< 0 and long v => AddEdge(node, dischargeNode, (ulong)Math.Min(Balance(node), -1 * v)),
				_  => throw new InvalidOperationException("Can't drain terminal nodes with 0 balance")
			};
	}
}
