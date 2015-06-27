/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 * 
 * Licensed under the Apache License, Version 2.0 (the "License"); 
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections;
using System.Collections.Generic;
using QuantConnect.Data;
using QuantConnect.Securities;

namespace QuantConnect.Lean.Engine.DataFeeds
{
    /// <summary>
    /// The FillForwardEnumerator wraps an existing base data enumerator and inserts extra 'base data' instances
    /// on a specified fill forward resolution
    /// </summary>
    public class FillForwardEnumerator : IEnumerator<BaseData>
    {
        private BaseData _previous;
        private bool _isFillingForward;
        private bool _emittedAuxilliaryData;
        
        private readonly DateTime _endTime;
        private readonly TimeSpan _dataResolution;
        private readonly TimeSpan _fillForwardResolution;
        private readonly SecurityExchange _exchange;
        private readonly bool _isExtendedMarketHours;
        private readonly IEnumerator<BaseData> _enumerator;

        /// <summary>
        /// Initializes a new instance of the <see cref="FillForwardEnumerator"/> class
        /// </summary>
        /// <param name="enumerator">The source enumerator to be filled forward</param>
        /// <param name="exchange">The exchange used to determine when to insert fill forward data</param>
        /// <param name="fillForwardResolution">The resolution we'd like to receive data on</param>
        /// <param name="isExtendedMarketHours">True to use the exchange's extended market hours, false to use the regular market hours</param>
        /// <param name="endTime">The end time of the subscrition, once passing this date the enumerator will stop</param>
        /// <param name="dataResolution">The source enumerator's data resolution</param>
        public FillForwardEnumerator(IEnumerator<BaseData> enumerator, 
            SecurityExchange exchange, 
            TimeSpan fillForwardResolution, 
            bool isExtendedMarketHours, 
            DateTime endTime, 
            TimeSpan dataResolution)
        {
            _endTime = endTime;
            _exchange = exchange;
            _enumerator = enumerator;
            _dataResolution = dataResolution;
            _fillForwardResolution = fillForwardResolution;
            _isExtendedMarketHours = isExtendedMarketHours;
        }

        /// <summary>
        /// Gets the element in the collection at the current position of the enumerator.
        /// </summary>
        /// <returns>
        /// The element in the collection at the current position of the enumerator.
        /// </returns>
        public BaseData Current
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the current element in the collection.
        /// </summary>
        /// <returns>
        /// The current element in the collection.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        object IEnumerator.Current
        {
            get { return Current; }
        }

        /// <summary>
        /// Advances the enumerator to the next element of the collection.
        /// </summary>
        /// <returns>
        /// true if the enumerator was successfully advanced to the next element; false if the enumerator has passed the end of the collection.
        /// </returns>
        /// <exception cref="T:System.InvalidOperationException">The collection was modified after the enumerator was created. </exception><filterpriority>2</filterpriority>
        public bool MoveNext()
        {
            if (!_emittedAuxilliaryData)
            {
                _previous = Current;
            }

            BaseData fillForward;

            if (!_isFillingForward)
            {
                // if we're filling forward we don't need to move next since we haven't emitted _enumerator.Current yet
                if (!_enumerator.MoveNext())
                {
                    // check to see if we ran out of data before the end of the subscription
                    if (_previous == null || _previous.EndTime >= _endTime)
                    {
                        // we passed the end of subscription, we're finished
                        return false;
                    }

                    // we can fill forward the rest of this subscription if required
                    var endOfSubscription = Current.Clone(true);
                    endOfSubscription.Time = _endTime - _dataResolution;
                    if (RequiresFillForwardData(_previous, endOfSubscription, out fillForward))
                    {
                        // don't mark as filling forward so we come back into this block, subscription is done
                        //_isFillingForward = true;
                        Current = fillForward;
                        return true;
                    }
                    
                    Current = endOfSubscription;
                    return true;
                }
            }

            if (_previous == null)
            {
                // first data point we dutifully emit without modification
                Current = _enumerator.Current;
                return true;
            }

            if (_enumerator.Current.DataType == MarketDataType.Auxiliary)
            {
                _emittedAuxilliaryData = true;
                Current = _enumerator.Current;
                return true;
            }
            
            _emittedAuxilliaryData = false;
            if (RequiresFillForwardData(_previous, _enumerator.Current, out fillForward))
            {
                // we require fill forward day because the _enumerator.Current is too far in future
                _isFillingForward = true;
                Current = fillForward;
                return true;
            }

            _isFillingForward = false;
            Current = _enumerator.Current;
            return true;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        /// <filterpriority>2</filterpriority>
        public void Dispose()
        {
            _enumerator.Dispose();
        }

        /// <summary>
        /// Sets the enumerator to its initial position, which is before the first element in the collection.
        /// </summary>
        /// <exception cref="T:System.InvalidOperationException">The collection was modified after the enumerator was created. </exception><filterpriority>2</filterpriority>
        public void Reset()
        {
            _enumerator.Reset();
        }

        /// <summary>
        /// Determines whether or not fill forward is required, and if true, will produce the new fill forward data
        /// </summary>
        /// <param name="previous">The last piece of data emitted by this enumerator</param>
        /// <param name="next">The next piece of data on the source enumerator</param>
        /// <param name="fillForward">When this function returns true, this will have a non-null value, null when the function returns false</param>
        /// <returns>True when a new fill forward piece of data was produced and should be emitted by this enumerator</returns>
        private bool RequiresFillForwardData(BaseData previous, BaseData next, out BaseData fillForward)
        {
            if (next.EndTime < previous.Time)
            {
                throw new ArgumentException();
            }

            // check to see if the gap between previous and next warrants fill forward behavior
            if (next.Time - previous.Time <= _fillForwardResolution)
            {
                fillForward = null;
                return false;
            }

            // is the bar after previous in market hours?
            var barAfterPreviousEndTime = previous.EndTime + _fillForwardResolution;
            if (_exchange.IsOpenDuringBar(previous.EndTime, barAfterPreviousEndTime, _isExtendedMarketHours))
            {
                fillForward = previous.Clone(true);
                fillForward.Time = previous.Time + _fillForwardResolution;
                return true;
            }

            var marketOpenNextDay = previous.EndTime.Date;
            if (previous.EndTime > GetMarketOpen(marketOpenNextDay) - _fillForwardResolution)
            {
                // after market hours on the same day, advance to the next emit time at market open
                marketOpenNextDay = GetMarketOpen(GetNextOpenDateAfter(barAfterPreviousEndTime.Date)) + _fillForwardResolution;
            }
            else
            {
                // the previous was before market open on the day we want to emit
                marketOpenNextDay = GetMarketOpen(marketOpenNextDay) + _fillForwardResolution;
            }

            if (marketOpenNextDay < next.EndTime)
            {
                // if next is still in the future then we need to emit a fill forward for market open
                fillForward = previous.Clone(true);
                fillForward.Time = (marketOpenNextDay - _dataResolution).RoundDown(_fillForwardResolution);
                return true;
            }

            // the next is before the next fill forward time, so do nothing
            fillForward = null;
            return false;
        }

        /// <summary>
        /// Finds the next open date that follows the specified date, this functions expects a date, not a date time
        /// </summary>
        private DateTime GetNextOpenDateAfter(DateTime date)
        {
            do
            {
                date = date + Time.OneDay;
            }
            while (!_exchange.DateIsOpen(date));
            return date;
        }

        /// <summary>
        /// Gets the market open for the specified date, this function expects a date, not a date time
        /// Takes into consideration regular/extended market hours
        /// </summary>
        private DateTime GetMarketOpen(DateTime date)
        {
            return date + (_isExtendedMarketHours ? _exchange.ExtendedMarketOpen : _exchange.MarketOpen);
        }
    }
}