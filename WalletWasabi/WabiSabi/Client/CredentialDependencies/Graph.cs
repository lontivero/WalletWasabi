using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using WalletWasabi.Helpers;

namespace WalletWasabi.WabiSabi.Client.CredentialDependencies
{
	public class DependencyGraph
	{
		public const int K = 2;

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

			// FIXME initialize members of array, they are currently null
			Successors = Enumerable.Repeat(0, (int)CredentialType.NumTypes).Select(_ => new Dictionary<int, HashSet<CredentialDependency>>()).ToImmutableArray();
			Predecessors = Enumerable.Repeat(0, (int)CredentialType.NumTypes).Select(_ => new Dictionary<int, HashSet<CredentialDependency>>()).ToImmutableArray();
			EdgeBalances = Enumerable.Repeat(0, (int)CredentialType.NumTypes).Select(_ => new Dictionary<int, long>()).ToImmutableArray();

			Vertices = ImmutableList<RequestNode>.Empty;

			foreach (var node in initialValuesImmutable.Select((values, i) => new RequestNode(i, values)))
			{
				// enforce at least one value != 0? all values? it doesn't actually matter for the algorithm
				AddNode(node);
			}
		}

		public ImmutableList<RequestNode> Vertices { get; private set; }

		// Internal properties used to keep track of effective values and edges
		private ImmutableArray<Dictionary<int, long>> EdgeBalances { get; }
		private ImmutableArray<Dictionary<int, HashSet<CredentialDependency>>> Successors { get; }
		private ImmutableArray<Dictionary<int, HashSet<CredentialDependency>>> Predecessors { get; }

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

		public IOrderedEnumerable<RequestNode> VerticesByBalance(CredentialType type) => Vertices.OrderByDescending(x => Balance(x, type));

		public IEnumerable<CredentialDependency> InEdges(RequestNode node, CredentialType credentialType) =>
			Predecessors[(int)credentialType][node.Id].ToImmutableArray();

		public IEnumerable<CredentialDependency> OutEdges(RequestNode node, CredentialType credentialType) =>
			Successors[(int)credentialType][node.Id].ToImmutableArray();

		private void AddNode(RequestNode node)
		{
			for (CredentialType credentialType = 0; credentialType < CredentialType.NumTypes; credentialType++)
			{
				var balances = EdgeBalances[(int)credentialType];
				if (balances.ContainsKey(node.Id))
				{
					throw new InvalidOperationException($"Node {node.Id} already exists in graph");
				}
				else
				{
					balances[node.Id] = 0;
				}

				foreach (var container in new[] { Successors[(int)credentialType], Predecessors[(int)credentialType] })
				{
					if (container.ContainsKey(node.Id))
					{
						throw new InvalidOperationException($"Node {node.Id} already exists in graph");
					}
					else
					{
						container[node.Id] = new HashSet<CredentialDependency>();
					}
				}
			}

			Vertices = Vertices.Add(node);
		}

		private RequestNode NewReissuanceNode()
		{
			var node = new RequestNode(Vertices.Count, Enumerable.Repeat(0L, (int)K).ToImmutableArray());
			AddNode(node);
			return node;
		}

		private long Balance(RequestNode node, CredentialType type) =>
			node.InitialBalance(type) + EdgeBalances[(int)type][node.Id];

		private int InDegree(RequestNode node, CredentialType credentialType) =>
			Predecessors[(int)credentialType][node.Id].Count;

		private int OutDegree(RequestNode node, CredentialType credentialType) =>
			Successors[(int)credentialType][node.Id].Count;

		private void AddEdge(CredentialDependency edge)
		{
			var successors = Successors[(int)edge.CredentialType][edge.From.Id];
			var predecessors = Predecessors[(int)edge.CredentialType][edge.To.Id];

			// Maintain subset of K-regular graph invariant
			if (successors.Count == K || predecessors.Count == K)
			{
				throw new InvalidOperationException("Can't add more than k edges");
			}

			// The edge sum invariant are only checked after the graph is
			// completed, it's too early to enforce that here without context
			// (ensuring that if all K
			EdgeBalances[(int)edge.CredentialType][edge.From.Id] -= (long)edge.Value;
			successors.Add(edge);

			EdgeBalances[(int)edge.CredentialType][edge.To.Id] += (long)edge.Value;
			predecessors.Add(edge);
		}

		// Drain values towards the center of the graph, propagating values
		// forwards or backwards corresponding to fan-in and fan out credenetial
		// dependencies.
		private void Drain(RequestNode x, IEnumerable<RequestNode> ys, CredentialType type)
		{
			// The nodes' initial balance determines edge direction. The given
			// nodes all have non-zero value and reissuance nodes always start
			// at 0. When x is a sink, we build a fan-in structure, and and when
			// x is a source it's an out.
			// We only check the first of the y nodes to see if we need to treat
			// x as a source or a sink.
			var xIsSink = x.InitialBalance(type).CompareTo(ys.First().InitialBalance(type)) == -1;

			foreach (var y in ys)
			{
				// The amount for the edge is always determined by the `y`
				// values, since we only add reissuance nodes to reduce the
				// number of values required.
				long amount = Balance(y, type);

				// TODO opportunistically drain larger credential types, this
				// will minimize dependencies between requests, weight
				// credentials should often be easily satisfiable with
				// parallel edges to the amount credential edges.
				if (xIsSink)
				{
					Guard.True(nameof(amount), amount > 0);
					AddEdge(new(y, x, type, (ulong)amount));
				}
				else
				{
					Guard.True(nameof(amount), amount != 0);
					Guard.True(nameof(amount), amount < 0);

					// When x is the source, we can only utilize its remaining
					// balance. The effective balance of the last y term term
					// might still have a negative magnitude after this.
					if (x.InitialBalance(type) == 0)
					{
						AddEdge(new(x, y, type, (ulong)(-1 * amount)));
					}
					else
					{
						AddEdge(new(x, y, type, (ulong)Math.Min(Balance(x, type), -1 * amount)));
					}
				}
			}
		}

		private void ResolveCredentials()
		{
			for (CredentialType credentialType = 0; credentialType < CredentialType.NumTypes; credentialType++)
			{
				// Stop when no negative valued nodes remain. The total sum is
				// positive, so by discharging elements of opposite values this
				// list is guaranteed to be reducible until empty.
				for (;;)
				{
					// Order the nodes of the graph based on their balances
					var ordered = VerticesByBalance(credentialType).ToImmutableArray();
					List<RequestNode> positive = ordered.Where(v => Balance(v, credentialType) > 0).ToList();
					List<RequestNode> negative = Enumerable.Reverse(ordered).Where(v => Balance(v, credentialType) < 0).ToList();

					if (negative.Count == 0)
					{
						break;
					}

					var nPositive = 1;
					var nNegative = 1;

					IEnumerable<RequestNode> posCandidates() => positive.Take(nPositive);
					IEnumerable<RequestNode> negCandidates() => negative.Take(nNegative);
					long posSum() => posCandidates().Sum(x => Balance(x, credentialType));
					long negSum() => negCandidates().Sum(x => Balance(x, credentialType));
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
					var maxCount = (compare() == 0 ? K : K - 1) - (largestIsSink ? InDegree(largestMagnitudeNode, credentialType) : OutDegree(largestMagnitudeNode, credentialType));
					Guard.True("small values all != 0", smallMagnitudeQueue.All(x => Balance(x, credentialType) != 0));
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

						Guard.True("nodes to combine should all have non-zero value", nodesToCombine.All(x => Balance(x, credentialType) != 0));

						// enqueue in their stead a reissuance node accounting
						// for their combined values, positive or negative.
						Drain(reissuance, nodesToCombine, credentialType);

						Guard.Same("combined nodes should be drained completely", string.Join(" ", Enumerable.Repeat(0L, nodesToCombine.Length)), string.Join(" ", nodesToCombine.Select(x => Balance(x, credentialType))));
						Guard.True("the reissuance node has a non-zero balance", Balance(reissuance, credentialType) != 0);
						Guard.True("everything left in the queue has a non-zero balance", smallMagnitudeQueue.All(x => Balance(x, credentialType) != 0));
						smallMagnitudeQueue.Add(reissuance);
						Guard.True("everything left in the queue has a non-zero balance", smallMagnitudeQueue.All(x => Balance(x, credentialType) != 0));
					}
					Guard.True("x", smallMagnitudeQueue.All(x => Balance(x, credentialType) != 0));

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
					if (Balance(smallMagnitudeQueue.Last(), credentialType) != 0)
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
				for (CredentialType credentialType = 0; credentialType < CredentialType.NumTypes; credentialType++)
				{
					var balance = Balance(node, credentialType);

					if (balance < 0)
					{
						throw new InvalidOperationException("Node must not have negative balance.");
					}

					var inDegree = InDegree(node, credentialType);
					if (inDegree > K)
					{
						// this is dead code, invariant enforced in AddEdge
						throw new InvalidOperationException("Node must not exceed degree K");
					}

					if (inDegree == K && Balance(node, credentialType) < 0)
					{
						throw new InvalidOperationException("Node with maximum in-degree must not have a negative balance.");
					}

					var outDegree = OutDegree(node, credentialType);
					if (outDegree > K)
					{
						// this is dead code, invariant enforced in AddEdge
						throw new InvalidOperationException("Node must not exceed degree K");
					}

					if (outDegree == K && Balance(node, credentialType) != 0)
					{
						throw new InvalidOperationException("Node with maximum out-degree must not have 0 balance");
					}
				}
			}
		}
	}
}
