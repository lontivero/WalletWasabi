using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using WalletWasabi.Helpers;

namespace WalletWasabi.WabiSabi.Client.CredentialDependencies
{
	record CredentialTypeTrack
	{
		public CredentialType CredentialType { get; init; }
		public ImmutableDictionary<int, long> EdgeBalances { get; init; } = ImmutableDictionary.Create<int, long>();
		public ImmutableDictionary<int, ImmutableHashSet<CredentialDependency>> Successors { get; init; } = ImmutableDictionary.Create<int, ImmutableHashSet<CredentialDependency>>();
		public ImmutableDictionary<int, ImmutableHashSet<CredentialDependency>> Predecessors { get; init;} = ImmutableDictionary.Create<int, ImmutableHashSet<CredentialDependency>>();

		public long Balance(RequestNode node) => node.InitialBalance(CredentialType) + EdgeBalances[node.Id];

		public int InDegree(int nodeId) => InEdges(nodeId).Count();

		public int OutDegree(int nodeId) => OutEdges(nodeId).Count();

		public IEnumerable<CredentialDependency> InEdges(int nodeId) =>	Predecessors[nodeId];

		public IEnumerable<CredentialDependency> OutEdges(int nodeId) => Successors[nodeId];

	}

	public class DependencyGraph
	{
		public const int K = ProtocolConstants.CredentialNumber;

		// Internal properties used to keep track of effective values and edges
		private ImmutableDictionary<CredentialType, CredentialTypeTrack> trackers;

		private DependencyGraph(IEnumerable<IEnumerable<long>> initialValues)
		{
			var initialValuesImmutable = initialValues.Select(x => x.ToImmutableArray()).ToImmutableArray();
			if (initialValuesImmutable.Any(x => x.Length != (int)CredentialType.NumTypes))
			{
				throw new ArgumentException($"Number of credential values must be {CredentialType.NumTypes}");
			}

			for (CredentialType i = 0; i < CredentialType.NumTypes; i++)
			{
				if (initialValuesImmutable.Sum(x => x[(int)i]) < 0)
				{
					throw new ArgumentException("Overall balance must not be negative");
				}
			}

			// per node entries created in AddNode, querying nodes not in the
			// graph should result in key errors.
			trackers = ImmutableDictionary<CredentialType, CredentialTypeTrack>.Empty
				.Add(CredentialType.Amount, new () { CredentialType = CredentialType.Amount })
				.Add(CredentialType.VirtualBytes, new () { CredentialType = CredentialType.VirtualBytes });

			foreach (var (values, i) in initialValuesImmutable.Select((values, i) => (values, i)))
			{
				// enforce at least one value != 0? all values? it doesn't actually matter for the algorithm
				AddNode(new RequestNode(i, values));
			}
		}

		public ImmutableList<RequestNode> Vertices { get; private set; } = ImmutableList<RequestNode>.Empty;

		public IOrderedEnumerable<RequestNode> VerticesByBalance(CredentialType credentialType) =>
			Vertices.OrderByDescending(node => trackers[credentialType].Balance(node));

		// TODO doc comment
		// Public API: construct a graph from amounts, and resolve the
		// credential dependencies. Should only produce valid graphs.
		// IDs are positive ints assigned in the order of the enumerable, but
		// Vertices will contain more elements if there are reissuance nodes.
		public static DependencyGraph ResolveCredentialDependencies(IEnumerable<IEnumerable<long>> amounts)
		{
			var graph = new DependencyGraph(amounts);
			graph.ResolveCredentials();
			return graph;
		}

		private void AddNode(RequestNode node)
		{
			foreach (var (credentialType, tracker) in trackers)
			{
				trackers = trackers.SetItem(
					credentialType,
					tracker with {
						EdgeBalances = tracker.EdgeBalances.Add(node.Id, 0),
						Successors = tracker.Successors.Add(node.Id, ImmutableHashSet<CredentialDependency>.Empty),
						Predecessors = tracker.Predecessors.Add(node.Id, ImmutableHashSet<CredentialDependency>.Empty)
					});
			}
			Vertices = Vertices.Add(node);
		}

		private RequestNode NewReissuanceNode()
		{
			var node = new RequestNode(Vertices.Count, Enumerable.Repeat(0L, (int)K).ToImmutableArray());
			AddNode(node);
			return node;
		}


		public void AddEdge(CredentialDependency edge)
		{
			var tracker = trackers[edge.CredentialType];
			var empty = ImmutableHashSet<CredentialDependency>.Empty;
			var successors = tracker.Successors.TryGetValue(edge.From.Id, out var val1) ? val1 : empty;
			var predecessors = tracker.Predecessors.TryGetValue(edge.To.Id, out var val2) ? val2 : empty;

			// Maintain subset of K-regular graph invariant
			if (successors.Count == K || predecessors.Count == K)
			{
				throw new InvalidOperationException("Can't add more than k edges");
			}

			// The edge sum invariant are only checked after the graph is
			// completed, it's too early to enforce that here without context
			// (ensuring that if all K
			tracker = tracker with {
				EdgeBalances = tracker.EdgeBalances
					.SetItem(edge.From.Id, tracker.EdgeBalances[edge.From.Id] - (long)edge.Value)
					.SetItem(edge.To.Id, tracker.EdgeBalances[edge.To.Id] + (long)edge.Value),
				Successors = tracker.Successors
					.SetItem(edge.From.Id, successors.Add(edge)),
				Predecessors = tracker.Predecessors
					.SetItem(edge.To.Id, predecessors.Add(edge))
				};
			trackers = trackers.SetItem(edge.CredentialType, tracker);
		}

		// Drain values towards the center of the graph, propagating values
		// forwards or backwards corresponding to fan-in and fan out credenetial
		// dependencies.
		private void Drain(RequestNode theNode, IEnumerable<RequestNode> nodes, CredentialType type)
		{
			// The nodes' initial balance determines edge direction. The given
			// nodes all have non-zero value and reissuance nodes always start
			// at 0. When theNode is a sink, we build a fan-in structure, and and when
			// theNode is a source it's an out.
			// We only check the first of the y nodes to see if we need to treat
			// theNode as a source or a sink.
			var theNodeIsSink = theNode.InitialBalance(type).CompareTo(nodes.First().InitialBalance(type)) == -1;

			var tracker = trackers[type];
			foreach (var node in nodes)
			{
				// The amount for the edge is always determined by the `y`
				// values, since we only add reissuance nodes to reduce the
				// number of values required.
				long amount = tracker.Balance(node);

				// TODO opportunistically drain larger credential types, this
				// will minimize dependencies between requests, weight
				// credentials should often be easily satisfiable with
				// parallel edges to the amount credential edges.
				if (theNodeIsSink)
				{
					Guard.True(nameof(amount), amount > 0);
					AddEdge(new(node, theNode, type, (ulong)amount));
				}
				else
				{
					Guard.True(nameof(amount), amount != 0);
					Guard.True(nameof(amount), amount < 0);

					// When theNode is the source, we can only utilize its remaining
					// balance. The effective balance of the last y term term
					// might still have a negative magnitude after this.
					if (theNode.InitialBalance(type) == 0)
					{
						AddEdge(new(theNode, node, type, (ulong)(-1 * amount)));
					}
					else
					{
						AddEdge(new(theNode, node, type, (ulong)Math.Min(tracker.Balance(theNode), -1 * amount)));
					}
				}
			}
		}

		private void ResolveCredentials()
		{
			foreach (var (credentialType, tracker) in trackers)
			{
				// Stop when no negative valued nodes remain. The total sum is
				// positive, so by discharging elements of opposite values this
				// list is guaranteed to be reducible until empty.
				for (;;)
				{
					// Order the nodes of the graph based on their balances
					var ordered = VerticesByBalance(credentialType).ToImmutableArray();
					List<RequestNode> positive = ordered.Where(node => tracker.Balance(node) > 0).ToList();
					List<RequestNode> negative = ordered.Where(node => tracker.Balance(node) < 0).Reverse().ToList();

					if (negative.Count == 0)
					{
						break;
					}

					var nPositive = 1;
					var nNegative = 1;

					IEnumerable<RequestNode> posCandidates() => positive.Take(nPositive);
					IEnumerable<RequestNode> negCandidates() => negative.Take(nNegative);
					long posSum() => posCandidates().Sum(node => tracker.Balance(node));
					long negSum() => negCandidates().Sum(node => tracker.Balance(node));
					long compare() => posSum().CompareTo(-1 * negSum());

					// Compare the first of each. we want to fully discharge the
					// larger (in absolute magnitude) of the two nodes, so we
					// will add more nodes to the smaller one until we can fully
					// cover. At each step of the iteration we fully discharge
					// at least 2 nodes from the queue.
					var initialComparison = compare();
					var fanIn = initialComparison == -1;

					if (initialComparison != 0)
					{
						Action takeOneMore = fanIn ? () => nPositive++ : () => nNegative++;

						// take more nodes until the comparison sign changes or
						// we run out.
						while (initialComparison == compare()
								 && (fanIn ? positive.Count >= 1 + nPositive
										   : negative.Count >= 1 + nNegative))
						{
							takeOneMore();
						}
					}

					var largestMagnitudeNode = (fanIn ? negative.Take(nNegative).Single() : positive.Take(nPositive).Single()); // assert n == 1?
					var smallMagnitudeQueue = (fanIn ? positive.Take(nPositive).Reverse() : negative.Take(nNegative)).ToList(); // reverse positive values so we always proceed in order of increasing magnitude
					var largestIsSink = largestMagnitudeNode.InitialBalance(credentialType).CompareTo(smallMagnitudeQueue.First().InitialBalance(credentialType)) == -1;
					var maxCount = (compare() == 0 ? K : K - 1) - (largestIsSink ? tracker.InDegree(largestMagnitudeNode.Id) : tracker.OutDegree(largestMagnitudeNode.Id));
					Guard.True("small values all != 0", smallMagnitudeQueue.All(node => tracker.Balance(node) != 0));
					negative.RemoveRange(0, nNegative);
					positive.RemoveRange(0, nPositive);

					// build a k-ary tree bottom up>
					// when the accumulated balance is even we can create k
					// edges, but if it's not exactly the same we'll need
					// one less for the remaining non-zero amount.
					while (smallMagnitudeQueue.Count > maxCount)
					{
						// add a new intermediate node
						var reissuance = NewReissuanceNode();

						Guard.Same("reissuance node initial balance", reissuance.InitialBalance(credentialType), 0L);

						// dequeue up to k nodes, possibly the entire queue. the
						// total number of items might be less than K but still
						// larger than maxCount (number of remaining slots in
						// the drained node)
						var take = Math.Min(K, smallMagnitudeQueue.Count);
						var nodesToCombine = smallMagnitudeQueue.Take(take).ToImmutableArray();
						smallMagnitudeQueue.RemoveRange(0, take);

						Guard.True("nodes to combine should all have non-zero value", nodesToCombine.All(node => tracker.Balance(node) != 0));

						// enqueue in their stead a reissuance node accounting
						// for their combined values, positive or negative.
						Drain(reissuance, nodesToCombine, credentialType);

						Guard.Same("combined nodes should be drained completely", string.Join(" ", Enumerable.Repeat(0L, nodesToCombine.Length)), string.Join(" ", nodesToCombine.Select(node => tracker.Balance(node))));
						Guard.True("the reissuance node has a non-zero balance", tracker.Balance(reissuance) != 0);
						Guard.True("everything left in the queue has a non-zero balance", smallMagnitudeQueue.All(node => tracker.Balance(node) != 0));
						smallMagnitudeQueue.Add(reissuance);
						Guard.True("everything left in the queue has a non-zero balance", smallMagnitudeQueue.All(node => tracker.Balance(node) != 0));
					}
					Guard.True("x", smallMagnitudeQueue.All(node => tracker.Balance(node) != 0));

					// When the queue has been reduced to this point, we can
					// actually cancel out negative and positive values. If this
					// is a fan in then the reissuance node will act as a sink
					// for the complete values of the prior nodes. If it's a fan
					// out, the sum of the smaller nodes' negative values can
					// exceed the value of the larger node, so the last one may
					// still have a negative value after draining the value from
					// the larger node.
					Drain(largestMagnitudeNode, smallMagnitudeQueue, credentialType);

					// Return the last smaller magnitude node if it's got a non 0 balance.
					// largestMagnitudeNode should be fully utilized so it never
					// needs to be returned when it has a non-zero balance,
					// because the stopping condition is determined only by
					// negative nodes having been eliminated.
					if (tracker.Balance(smallMagnitudeQueue.Last()) != 0)
					{
						(fanIn ? negative : positive).Add(smallMagnitudeQueue.Last());
					}
				}

				// at this point the sub-graph of credentialType edges should be
				// a planar DAG with the AssertResolvedGraphInvariants() holding for
				// that particular type.
			}

			// at this point the entire graoh should be a DAG with labeled
			// edges that can be partitioned into NumTypes different planar
			// DAGs, and the invariants should hold for all of these.
			AssertResolvedGraphInvariants();
		}

		public void AssertResolvedGraphInvariants()
		{
			// TODO doc comment. summary? description?
			// Ensure resolved graph invariants hold:
			// - no degree > k
			// - degree k nodes fully discharged (no implicit leftover
			//   amount without room for zero credential)
			// - no negative balances (relax?)
			foreach (var node in Vertices)
			{
				foreach (var (credentialType, tracker) in trackers)
				{
					var balance = tracker.Balance(node);

					if (balance < 0)
					{
						throw new InvalidOperationException("Node must not have negative balance.");
					}

					var inDegree = tracker.InDegree(node.Id);
					if (inDegree > K)
					{
						// this is dead code, invariant enforced in AddEdge
						throw new InvalidOperationException("Node must not exceed degree K");
					}

					if (inDegree == K && tracker.Balance(node) < 0)
					{
						throw new InvalidOperationException("Node with maximum in-degree must not have a negative balance.");
					}

					var outDegree = tracker.OutDegree(node.Id);
					if (outDegree > K)
					{
						// this is dead code, invariant enforced in AddEdge
						throw new InvalidOperationException("Node must not exceed degree K");
					}

					if (outDegree == K && tracker.Balance(node) != 0)
					{
						throw new InvalidOperationException("Node with maximum out-degree must not have 0 balance");
					}
				}
			}
		}
	}
}
