using Xunit;

// These tests each construct an InProcessExecutionClient, and every one of those creates the console's
// "alpha" book under the FIXED account simulated/execution-console-alpha. Acquiring that book's lease
// takes a machine-wide named mutex (Global\DaxAlgoTerminal.Execution.Account.<sha>) — the product's
// one-writer-per-broker-account guard. Two test classes running concurrently therefore fight over a
// singleton and the loser fails with "Another same-machine writer owns the execution account lease".
//
// That is what produced the shifting handful of red tests in a full-solution run while the assembly
// passed on its own: the collision only happens when the classes actually overlap. Serialising the
// assembly removes the overlap. It costs a little wall-clock and buys a deterministic suite.
//
// Do NOT "fix" this by weakening the mutex — the guard is a money-path safety property.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
