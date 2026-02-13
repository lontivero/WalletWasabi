namespace WalletWasabi.Fluent.Models;

public enum HealthMonitorState
{
	Loading,
	Ready,
	UpdateAvailable,
	IndexerConnectionIssueDetected,
	BitcoinRpcIssueDetected,
	BitcoinRpcSynchronizing,
}
