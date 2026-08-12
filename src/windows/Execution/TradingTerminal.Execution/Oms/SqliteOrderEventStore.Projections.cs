using Microsoft.Data.Sqlite;

namespace TradingTerminal.Execution.Oms;

public sealed partial class SqliteOrderEventStore
{
    private void RebuildAggregateProjections(
        IReadOnlyList<OrderEvent> events,
        OrderProjection projection,
        SqliteTransaction transaction,
        bool deleteExisting = true)
    {
        if (deleteExisting)
            ClearAggregateProjections(projection.ClientOrderId, transaction);

        InsertIntent(events[0], projection.Instruction, transaction);
        InsertOrderProjection(projection, transaction);
        foreach (var orderEvent in events)
        {
            if (orderEvent.Fill.HasValue)
                InsertFillProjections(orderEvent, projection.Instruction, transaction);
            if (orderEvent.RiskDecision.HasValue)
                InsertRiskDecision(orderEvent, transaction);
            if (orderEvent.Reconciliation.HasValue)
                InsertReconciliationResolution(orderEvent, transaction);
        }
    }

    private void ClearEventDerivedProjections(SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM fees_commissions;
            DELETE FROM position_lots;
            DELETE FROM fills;
            DELETE FROM risk_decisions;
            DELETE FROM reconciliation_cases WHERE source_order_sequence IS NOT NULL;
            DELETE FROM orders;
            DELETE FROM order_intents;
            """;
        command.ExecuteNonQuery();
    }

    private void ClearAggregateProjections(ClientOrderId aggregateId, SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM fees_commissions WHERE aggregate_id = $aggregateId;
            DELETE FROM position_lots WHERE aggregate_id = $aggregateId;
            DELETE FROM fills WHERE aggregate_id = $aggregateId;
            DELETE FROM risk_decisions WHERE aggregate_id = $aggregateId;
            DELETE FROM reconciliation_cases
            WHERE client_order_id = $aggregateId AND source_order_sequence IS NOT NULL;
            DELETE FROM orders WHERE client_order_id = $aggregateId;
            DELETE FROM order_intents WHERE client_order_id = $aggregateId;
            """;
        AddParameter(command, "$aggregateId", aggregateId.Value);
        command.ExecuteNonQuery();
    }

    private void InsertIntent(
        OrderEvent firstEvent,
        CanonicalOrderInstruction instruction,
        SqliteTransaction transaction)
    {
        var identity = instruction.Identity;
        var intent = instruction.TradeIntent;
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO order_intents(
                intent_id, client_order_id, bucket_id, leg_id, correlation_id,
                originating_causation_id, execution_lease_id, fencing_token,
                instrument_id, quantity_mode, signed_units_coefficient, signed_units_scale,
                protective_stop_coefficient, protective_stop_scale,
                profit_target_coefficient, profit_target_scale,
                estimated_cost_coefficient, estimated_cost_scale,
                strategy_id, strategy_note_id, policy_version, source_sequence, source_event_hash)
            VALUES (
                $intentId, $clientOrderId, $bucketId, $legId, $correlationId,
                $originatingCausationId, $executionLeaseId, $fencingToken,
                $instrumentId, $quantityMode, $signedUnitsCoefficient, $signedUnitsScale,
                $protectiveStopCoefficient, $protectiveStopScale,
                $profitTargetCoefficient, $profitTargetScale,
                $estimatedCostCoefficient, $estimatedCostScale,
                $strategyId, $strategyNoteId, $policyVersion, $sourceSequence, $sourceEventHash);
            """;
        AddParameter(command, "$intentId", identity.IntentId.Value);
        AddParameter(command, "$clientOrderId", identity.ClientOrderId.Value);
        AddParameter(command, "$bucketId", identity.BucketId?.Value);
        AddParameter(command, "$legId", identity.LegId.Value);
        AddParameter(command, "$correlationId", identity.CorrelationId.Value);
        AddParameter(command, "$originatingCausationId", identity.CausationId.Value);
        AddParameter(command, "$executionLeaseId", identity.ExecutionLeaseId.Value);
        AddParameter(command, "$fencingToken", identity.FencingToken.Value);
        AddParameter(command, "$instrumentId", intent.Instrument.Value);
        AddParameter(command, "$quantityMode", (int)intent.QuantityMode);
        AddParameter(command, "$signedUnitsCoefficient", intent.SignedUnits.Coefficient);
        AddParameter(command, "$signedUnitsScale", intent.SignedUnits.Scale);
        AddParameter(command, "$protectiveStopCoefficient", intent.ProtectiveStopPrice?.Coefficient);
        AddParameter(command, "$protectiveStopScale", intent.ProtectiveStopPrice?.Scale);
        AddParameter(command, "$profitTargetCoefficient", intent.ProfitTargetPrice?.Coefficient);
        AddParameter(command, "$profitTargetScale", intent.ProfitTargetPrice?.Scale);
        AddParameter(command, "$estimatedCostCoefficient", intent.EstimatedRoundTripCostPerUnit.Coefficient);
        AddParameter(command, "$estimatedCostScale", intent.EstimatedRoundTripCostPerUnit.Scale);
        AddParameter(command, "$strategyId", intent.StrategyId);
        AddParameter(command, "$strategyNoteId", intent.StrategyNoteId);
        AddParameter(command, "$policyVersion", intent.PolicyVersion);
        AddParameter(command, "$sourceSequence", firstEvent.AggregateSequence);
        AddParameter(command, "$sourceEventHash", firstEvent.EventHash);
        command.ExecuteNonQuery();
    }

    private void InsertOrderProjection(OrderProjection projection, SqliteTransaction transaction)
    {
        var terms = projection.Terms;
        var replacement = projection.ReplacementTerms;
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO orders(
                client_order_id, intent_id, state, side, order_type, time_in_force,
                quantity_coefficient, quantity_scale,
                limit_price_coefficient, limit_price_scale, stop_price_coefficient, stop_price_scale,
                replacement_side, replacement_order_type, replacement_time_in_force,
                replacement_quantity_coefficient, replacement_quantity_scale,
                replacement_limit_coefficient, replacement_limit_scale,
                replacement_stop_coefficient, replacement_stop_scale,
                broker_order_id, exchange_order_id,
                filled_quantity_coefficient, filled_quantity_scale,
                total_fees_coefficient, total_fees_scale,
                last_sequence, last_event_hash, last_causation_id, projection_payload_json)
            VALUES (
                $clientOrderId, $intentId, $state, $side, $orderType, $timeInForce,
                $quantityCoefficient, $quantityScale,
                $limitCoefficient, $limitScale, $stopCoefficient, $stopScale,
                $replacementSide, $replacementOrderType, $replacementTimeInForce,
                $replacementQuantityCoefficient, $replacementQuantityScale,
                $replacementLimitCoefficient, $replacementLimitScale,
                $replacementStopCoefficient, $replacementStopScale,
                $brokerOrderId, $exchangeOrderId,
                $filledQuantityCoefficient, $filledQuantityScale,
                $totalFeesCoefficient, $totalFeesScale,
                $lastSequence, $lastEventHash, $lastCausationId, $projectionPayload);
            """;
        AddParameter(command, "$clientOrderId", projection.ClientOrderId.Value);
        AddParameter(command, "$intentId", projection.Instruction.Identity.IntentId.Value);
        AddParameter(command, "$state", (int)projection.State);
        AddParameter(command, "$side", (int)terms.Side);
        AddParameter(command, "$orderType", (int)terms.OrderType);
        AddParameter(command, "$timeInForce", (int)terms.TimeInForce);
        AddParameter(command, "$quantityCoefficient", terms.Quantity.Coefficient);
        AddParameter(command, "$quantityScale", terms.Quantity.Scale);
        AddParameter(command, "$limitCoefficient", terms.LimitPrice?.Coefficient);
        AddParameter(command, "$limitScale", terms.LimitPrice?.Scale);
        AddParameter(command, "$stopCoefficient", terms.StopPrice?.Coefficient);
        AddParameter(command, "$stopScale", terms.StopPrice?.Scale);
        AddParameter(command, "$replacementSide", replacement.HasValue ? (int)replacement.Value.Side : null);
        AddParameter(command, "$replacementOrderType", replacement.HasValue ? (int)replacement.Value.OrderType : null);
        AddParameter(command, "$replacementTimeInForce", replacement.HasValue ? (int)replacement.Value.TimeInForce : null);
        AddParameter(command, "$replacementQuantityCoefficient", replacement?.Quantity.Coefficient);
        AddParameter(command, "$replacementQuantityScale", replacement?.Quantity.Scale);
        AddParameter(command, "$replacementLimitCoefficient", replacement?.LimitPrice?.Coefficient);
        AddParameter(command, "$replacementLimitScale", replacement?.LimitPrice?.Scale);
        AddParameter(command, "$replacementStopCoefficient", replacement?.StopPrice?.Coefficient);
        AddParameter(command, "$replacementStopScale", replacement?.StopPrice?.Scale);
        AddParameter(command, "$brokerOrderId", projection.BrokerOrderId?.Value);
        AddParameter(command, "$exchangeOrderId", projection.ExchangeOrderId?.Value);
        AddParameter(command, "$filledQuantityCoefficient", projection.FilledQuantity.Coefficient);
        AddParameter(command, "$filledQuantityScale", projection.FilledQuantity.Scale);
        AddParameter(command, "$totalFeesCoefficient", projection.TotalFees.Coefficient);
        AddParameter(command, "$totalFeesScale", projection.TotalFees.Scale);
        AddParameter(command, "$lastSequence", projection.LastSequence);
        AddParameter(command, "$lastEventHash", projection.LastEventHash);
        AddParameter(command, "$lastCausationId", projection.LastCausationId.Value);
        AddParameter(command, "$projectionPayload", Serialize(projection));
        command.ExecuteNonQuery();
    }

    private void InsertFillProjections(
        OrderEvent orderEvent,
        CanonicalOrderInstruction instruction,
        SqliteTransaction transaction)
    {
        var fill = orderEvent.Fill!.Value;
        using (var command = _writeConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO fills(
                    aggregate_id, aggregate_sequence,
                    quantity_coefficient, quantity_scale, price_coefficient, price_scale,
                    fee_coefficient, fee_scale, liquidity, occurred_at_utc_ticks,
                    broker_order_id, exchange_order_id)
                VALUES (
                    $aggregateId, $aggregateSequence,
                    $quantityCoefficient, $quantityScale, $priceCoefficient, $priceScale,
                    $feeCoefficient, $feeScale, $liquidity, $occurredAt,
                    $brokerOrderId, $exchangeOrderId);
                """;
            AddParameter(command, "$aggregateId", orderEvent.AggregateId.Value);
            AddParameter(command, "$aggregateSequence", orderEvent.AggregateSequence);
            AddParameter(command, "$quantityCoefficient", fill.Quantity.Coefficient);
            AddParameter(command, "$quantityScale", fill.Quantity.Scale);
            AddParameter(command, "$priceCoefficient", fill.Price.Coefficient);
            AddParameter(command, "$priceScale", fill.Price.Scale);
            AddParameter(command, "$feeCoefficient", fill.Fee.Coefficient);
            AddParameter(command, "$feeScale", fill.Fee.Scale);
            AddParameter(command, "$liquidity", (int)fill.Liquidity);
            AddParameter(command, "$occurredAt", orderEvent.OccurredAtUtc.Ticks);
            AddParameter(command, "$brokerOrderId", orderEvent.BrokerOrderId?.Value);
            AddParameter(command, "$exchangeOrderId", orderEvent.ExchangeOrderId?.Value);
            command.ExecuteNonQuery();
        }

        using (var command = _writeConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO fees_commissions(aggregate_id, fill_sequence, amount_coefficient, amount_scale)
                VALUES ($aggregateId, $fillSequence, $amountCoefficient, $amountScale);
                """;
            AddParameter(command, "$aggregateId", orderEvent.AggregateId.Value);
            AddParameter(command, "$fillSequence", orderEvent.AggregateSequence);
            AddParameter(command, "$amountCoefficient", fill.Fee.Coefficient);
            AddParameter(command, "$amountScale", fill.Fee.Scale);
            command.ExecuteNonQuery();
        }

        using (var command = _writeConnection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO position_lots(
                    aggregate_id, fill_sequence, instrument_id, side,
                    quantity_coefficient, quantity_scale,
                    fill_price_coefficient, fill_price_scale,
                    fee_coefficient, fee_scale, source_event_hash)
                VALUES (
                    $aggregateId, $fillSequence, $instrumentId, $side,
                    $quantityCoefficient, $quantityScale,
                    $fillPriceCoefficient, $fillPriceScale,
                    $feeCoefficient, $feeScale, $sourceEventHash);
                """;
            AddParameter(command, "$aggregateId", orderEvent.AggregateId.Value);
            AddParameter(command, "$fillSequence", orderEvent.AggregateSequence);
            AddParameter(command, "$instrumentId", instruction.TradeIntent.Instrument.Value);
            AddParameter(command, "$side", (int)instruction.Terms.Side);
            AddParameter(command, "$quantityCoefficient", fill.Quantity.Coefficient);
            AddParameter(command, "$quantityScale", fill.Quantity.Scale);
            AddParameter(command, "$fillPriceCoefficient", fill.Price.Coefficient);
            AddParameter(command, "$fillPriceScale", fill.Price.Scale);
            AddParameter(command, "$feeCoefficient", fill.Fee.Coefficient);
            AddParameter(command, "$feeScale", fill.Fee.Scale);
            AddParameter(command, "$sourceEventHash", orderEvent.EventHash);
            command.ExecuteNonQuery();
        }
    }

    private void InsertRiskDecision(OrderEvent orderEvent, SqliteTransaction transaction)
    {
        var decision = orderEvent.RiskDecision!.Value;
        var limits = decision.PolicyLimits;
        var input = decision.Input;
        var before = decision.ExposureBefore;
        var after = decision.ExposureAfter;
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO risk_decisions(
                aggregate_id, aggregate_sequence, policy_id, policy_version, policy_hash,
                outcome, reason_codes,
                maximum_order_quantity_coefficient, maximum_order_quantity_scale,
                maximum_order_notional_coefficient, maximum_order_notional_scale,
                maximum_position_coefficient, maximum_position_scale,
                maximum_gross_exposure_coefficient, maximum_gross_exposure_scale,
                daily_loss_limit_coefficient, daily_loss_limit_scale,
                input_position_coefficient, input_position_scale,
                input_reference_price_coefficient, input_reference_price_scale,
                input_contract_multiplier_coefficient, input_contract_multiplier_scale,
                input_gross_exposure_coefficient, input_gross_exposure_scale,
                input_realized_pnl_coefficient, input_realized_pnl_scale,
                input_mark_to_market_pnl_coefficient, input_mark_to_market_pnl_scale,
                risk_day_number, input_is_complete,
                signed_order_quantity_coefficient, signed_order_quantity_scale,
                order_notional_coefficient, order_notional_scale,
                exposure_before_position_coefficient, exposure_before_position_scale,
                exposure_before_instrument_coefficient, exposure_before_instrument_scale,
                exposure_before_gross_coefficient, exposure_before_gross_scale,
                exposure_after_position_coefficient, exposure_after_position_scale,
                exposure_after_instrument_coefficient, exposure_after_instrument_scale,
                exposure_after_gross_coefficient, exposure_after_gross_scale,
                decision_payload_json, source_event_hash)
            VALUES (
                $aggregateId, $aggregateSequence, $policyId, $policyVersion, $policyHash,
                $outcome, $reasonCodes,
                $maximumOrderQuantityCoefficient, $maximumOrderQuantityScale,
                $maximumOrderNotionalCoefficient, $maximumOrderNotionalScale,
                $maximumPositionCoefficient, $maximumPositionScale,
                $maximumGrossExposureCoefficient, $maximumGrossExposureScale,
                $dailyLossLimitCoefficient, $dailyLossLimitScale,
                $inputPositionCoefficient, $inputPositionScale,
                $inputReferencePriceCoefficient, $inputReferencePriceScale,
                $inputContractMultiplierCoefficient, $inputContractMultiplierScale,
                $inputGrossExposureCoefficient, $inputGrossExposureScale,
                $inputRealizedPnlCoefficient, $inputRealizedPnlScale,
                $inputMarkToMarketPnlCoefficient, $inputMarkToMarketPnlScale,
                $riskDayNumber, $inputIsComplete,
                $signedOrderQuantityCoefficient, $signedOrderQuantityScale,
                $orderNotionalCoefficient, $orderNotionalScale,
                $beforePositionCoefficient, $beforePositionScale,
                $beforeInstrumentCoefficient, $beforeInstrumentScale,
                $beforeGrossCoefficient, $beforeGrossScale,
                $afterPositionCoefficient, $afterPositionScale,
                $afterInstrumentCoefficient, $afterInstrumentScale,
                $afterGrossCoefficient, $afterGrossScale,
                $decisionPayload, $sourceEventHash);
            """;
        AddParameter(command, "$aggregateId", orderEvent.AggregateId.Value);
        AddParameter(command, "$aggregateSequence", orderEvent.AggregateSequence);
        AddParameter(command, "$policyId", decision.PolicyId);
        AddParameter(command, "$policyVersion", decision.PolicyVersion);
        AddParameter(command, "$policyHash", decision.PolicyHash);
        AddParameter(command, "$outcome", (int)decision.Outcome);
        AddParameter(command, "$reasonCodes", (int)decision.ReasonCodes);
        AddExact(command, "$maximumOrderQuantity", limits.MaximumOrderQuantity.Coefficient, limits.MaximumOrderQuantity.Scale);
        AddExact(command, "$maximumOrderNotional", limits.MaximumOrderNotional.Coefficient, limits.MaximumOrderNotional.Scale);
        AddExact(command, "$maximumPosition", limits.MaximumAbsolutePositionPerInstrument.Coefficient, limits.MaximumAbsolutePositionPerInstrument.Scale);
        AddExact(command, "$maximumGrossExposure", limits.MaximumGrossExposure.Coefficient, limits.MaximumGrossExposure.Scale);
        AddExact(command, "$dailyLossLimit", limits.DailyLossLimit.Coefficient, limits.DailyLossLimit.Scale);
        AddExact(command, "$inputPosition", input.PositionBefore.Coefficient, input.PositionBefore.Scale);
        AddExact(command, "$inputReferencePrice", input.ReferencePrice.Coefficient, input.ReferencePrice.Scale);
        AddExact(command, "$inputContractMultiplier", input.ContractMultiplier.Coefficient, input.ContractMultiplier.Scale);
        AddExact(command, "$inputGrossExposure", input.GrossExposureBefore.Coefficient, input.GrossExposureBefore.Scale);
        AddExact(command, "$inputRealizedPnl", input.DailyRealizedPnl.Coefficient, input.DailyRealizedPnl.Scale);
        AddExact(command, "$inputMarkToMarketPnl", input.DailyMarkToMarketPnl.Coefficient, input.DailyMarkToMarketPnl.Scale);
        AddParameter(command, "$riskDayNumber", input.RiskDay.DayNumber);
        AddParameter(command, "$inputIsComplete", input.IsComplete ? 1 : 0);
        AddExact(command, "$signedOrderQuantity", decision.SignedOrderQuantity.Coefficient, decision.SignedOrderQuantity.Scale);
        AddExact(command, "$orderNotional", decision.OrderNotional.Coefficient, decision.OrderNotional.Scale);
        AddExact(command, "$beforePosition", before.Position.Coefficient, before.Position.Scale);
        AddExact(command, "$beforeInstrument", before.InstrumentExposure.Coefficient, before.InstrumentExposure.Scale);
        AddExact(command, "$beforeGross", before.GrossExposure.Coefficient, before.GrossExposure.Scale);
        AddExact(command, "$afterPosition", after.Position.Coefficient, after.Position.Scale);
        AddExact(command, "$afterInstrument", after.InstrumentExposure.Coefficient, after.InstrumentExposure.Scale);
        AddExact(command, "$afterGross", after.GrossExposure.Coefficient, after.GrossExposure.Scale);
        AddParameter(command, "$decisionPayload", Serialize(decision));
        AddParameter(command, "$sourceEventHash", orderEvent.EventHash);
        command.ExecuteNonQuery();
    }

    private void InsertReconciliationResolution(OrderEvent orderEvent, SqliteTransaction transaction)
    {
        var resolution = orderEvent.Reconciliation!.Value;
        var factSequence = checked(-orderEvent.AggregateSequence);
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reconciliation_cases(
                case_id, fact_sequence, client_order_id, evidence,
                source_order_sequence, observed_state, source_event_hash)
            VALUES (
                $caseId, $factSequence, $clientOrderId, $evidence,
                $sourceOrderSequence, $observedState, $sourceEventHash);
            """;
        AddParameter(command, "$caseId", resolution.CaseId.Value);
        AddParameter(command, "$factSequence", factSequence);
        AddParameter(command, "$clientOrderId", orderEvent.AggregateId.Value);
        AddParameter(command, "$evidence", resolution.Evidence);
        AddParameter(command, "$sourceOrderSequence", orderEvent.AggregateSequence);
        AddParameter(command, "$observedState", (int)resolution.ObservedState);
        AddParameter(command, "$sourceEventHash", orderEvent.EventHash);
        command.ExecuteNonQuery();
    }

    private long ReadNextReconciliationFactSequence(
        ReconciliationCaseId caseId,
        SqliteTransaction transaction)
    {
        using var command = _writeConnection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(MAX(fact_sequence), 0)
            FROM reconciliation_cases
            WHERE case_id = $caseId AND fact_sequence > 0;
            """;
        AddParameter(command, "$caseId", caseId.Value);
        var current = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (current == long.MaxValue)
            throw new InvalidDataException("The reconciliation-case fact sequence is exhausted.");
        return current + 1;
    }

    private static void AddExact(SqliteCommand command, string prefix, long coefficient, byte scale)
    {
        AddParameter(command, $"{prefix}Coefficient", coefficient);
        AddParameter(command, $"{prefix}Scale", scale);
    }
}
