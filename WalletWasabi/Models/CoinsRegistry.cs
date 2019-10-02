using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using NBitcoin;
using WalletWasabi.Models;

namespace WalletWasabi.Gui.Models
{
	public class CoinsRegistry
	{
		private HashSet<SmartCoin> Coins { get; }
		private HashSet<SmartCoin> LatestCoinsSnapshot { get; set; }
		private bool InvalidateSnapshot { get; set; }
		private object Lock { get; set; }
		public event NotifyCollectionChangedEventHandler CollectionChanged;

		public CoinsRegistry()
		{
			Coins = new HashSet<SmartCoin>();
			LatestCoinsSnapshot = new HashSet<SmartCoin>();
			InvalidateSnapshot = false;
			Lock = new object();
		}

		public CoinsView AsCoinsView()
		{
			lock(Lock)
			{
				if(InvalidateSnapshot)
				{
					LatestCoinsSnapshot = new HashSet<SmartCoin>(Coins);
					InvalidateSnapshot = false;
				}
			}
			return new CoinsView(LatestCoinsSnapshot);
		}

		public bool IsEmpty => !AsCoinsView().Any();

		public SmartCoin GetByOutPoint(OutPoint outpoint)
		{
			return AsCoinsView().FirstOrDefault(x => x.GetOutPoint() == outpoint);
		}

		public bool TryAdd(SmartCoin coin)
		{
			var added = false;
			lock (Lock)
			{
				added = Coins.Add(coin);
				InvalidateSnapshot |= added ;
			}

			if (added)
			{
				CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, coin));
			}
			return added;
		}

		public void Remove(SmartCoin coin)
		{
			var coinsToRemove = AsCoinsView().DescendantOf(coin).ToList();
			coinsToRemove.Add(coin);
			lock (Lock)
			{
				foreach (var toRemove in coinsToRemove)
				{
					Coins.Remove(coin);
				}
				InvalidateSnapshot = true;
			}
			CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, coinsToRemove));
		}

		public void Clear()
		{
			lock (Lock)
			{
				Coins.Clear();
				InvalidateSnapshot = true;
			}
			CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
		}
	}
}
