﻿// Copyright (c) 2002-2018 "Neo4j,"
// Neo4j Sweden AB [http://neo4j.com]
// 
// This file is part of Neo4j.
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Neo4j.Driver.Internal.Connector;
using Neo4j.Driver.Internal.Metrics;
using Neo4j.Driver.V1;
using static Neo4j.Driver.Internal.ConnectionPoolStatus;
using static Neo4j.Driver.Internal.Throw.ObjectDisposedException;

namespace Neo4j.Driver.Internal
{
    internal sealed class ConnectionPoolStatus
    {
        public static readonly ConnectionPoolStatus Active = new ConnectionPoolStatus(PoolStatus.Open);
        public static readonly ConnectionPoolStatus Closed = new ConnectionPoolStatus(PoolStatus.Closed);
        public static readonly ConnectionPoolStatus Inactive = new ConnectionPoolStatus(PoolStatus.Inactive);

        public readonly PoolStatus Code;

        private ConnectionPoolStatus(PoolStatus code)
        {
            Code = code;
        }
    }

    internal class ConnectionPool : LoggerBase, IConnectionPool
    {
        private const int SpinningWaitInterval = 500;

        private readonly Uri _uri;

        private ConnectionPoolStatus _poolStatus = Active;
        private bool IsClosed => AtomicRead(ref _poolStatus) == Closed;
        private bool IsInactive => AtomicRead(ref _poolStatus) == Inactive;
        private bool IsInactiveOrClosed => AtomicRead(ref _poolStatus) != Active;

        private int _poolSize = 0;
        private readonly int _maxPoolSize;
        private readonly int _maxIdlePoolSize;
        private readonly TimeSpan _connAcquisitionTimeout;

        private readonly IConnectionValidator _connectionValidator;
        private readonly IPooledConnectionFactory _connectionFactory;

        private readonly BlockingCollection<IPooledConnection> _idleConnections = new BlockingCollection<IPooledConnection>();
        private readonly ConcurrentSet<IPooledConnection> _inUseConnections = new ConcurrentSet<IPooledConnection>();

        private readonly IConnectionPoolListener _poolMetricsListener;
        private readonly IConnectionListener _connectionMetricsListener;

        public int NumberOfInUseConnections => _inUseConnections.Count;
        public int NumberOfIdleConnections => _idleConnections.Count;
        internal int PoolSize => Interlocked.CompareExchange(ref _poolSize, -1, -1);

        public ConnectionPoolStatus Status
        {
            get => AtomicRead(ref _poolStatus);
            internal set => Interlocked.Exchange(ref _poolStatus, value);
        }

        public ConnectionPool(
            Uri uri,
            IPooledConnectionFactory connectionFactory,
            ConnectionPoolSettings connectionPoolSettings,
            ILogger logger)
            : base(logger)
        {
            _uri = uri;

            _maxPoolSize = connectionPoolSettings.MaxConnectionPoolSize;
            _maxIdlePoolSize = connectionPoolSettings.MaxIdleConnectionPoolSize;
            _connAcquisitionTimeout = connectionPoolSettings.ConnectionAcquisitionTimeout;
            _connectionFactory = connectionFactory;

            var connIdleTimeout = connectionPoolSettings.ConnectionIdleTimeout;
            var maxConnectionLifetime = connectionPoolSettings.MaxConnectionLifetime;
            _connectionValidator = new ConnectionValidator(connIdleTimeout, maxConnectionLifetime);

            var metrics = connectionPoolSettings.Metrics;
            _poolMetricsListener = metrics?.CreateConnectionPoolListener(uri, this);
            _connectionMetricsListener = metrics?.CreateConnectionListener(uri);
        }

        internal ConnectionPool(
            IPooledConnectionFactory connectionFactory,
            BlockingCollection<IPooledConnection> idleConnections = null,
            ConcurrentSet<IPooledConnection> inUseConnections = null,
            ConnectionPoolSettings poolSettings = null,
            IConnectionValidator validator = null,
            ILogger logger = null)
            : this(null, connectionFactory, poolSettings ?? new ConnectionPoolSettings(Config.DefaultConfig), logger)
        {
            _idleConnections = idleConnections ?? new BlockingCollection<IPooledConnection>();
            _inUseConnections = inUseConnections ?? new ConcurrentSet<IPooledConnection>();
            if (validator != null)
            {
                _connectionValidator = validator;
            }
        }

        private IPooledConnection CreateNewPooledConnection()
        {
            _poolMetricsListener?.ConnectionCreating();
            IPooledConnection conn = null;
            try
            {
                conn = NewPooledConnection();
                if (conn != null)
                {
                    conn.Init();
                    _poolMetricsListener?.ConnectionCreated();
                    return conn;
                }
            }
            catch
            {
                _poolMetricsListener?.ConnectionFailedToCreate();

                // shut down and clean all the resources of the connection if failed to establish
                DestroyConnection(conn);
                throw;
            }

            return null;
        }

        private async Task<IPooledConnection> CreateNewPooledConnectionAsync()
        {
            _poolMetricsListener?.ConnectionCreating();
            IPooledConnection conn = null;
            try
            {
                conn = NewPooledConnection();
                if (conn != null)
                {
                    await conn.InitAsync().ConfigureAwait(false);
                    _poolMetricsListener?.ConnectionCreated();
                    return conn;
                }
            }
            catch
            {
                _poolMetricsListener?.ConnectionFailedToCreate();

                // shut down and clean all the resources of the connection if failed to establish
                await DestroyConnectionAsync(conn).ConfigureAwait(false);
                throw;
            }

            return null;
        }

        private IPooledConnection NewPooledConnection()
        {
            if (TryIncrementPoolSize())
            {
                return _connectionFactory.Create(_uri, this, _connectionMetricsListener);
            }
            return null;
        }

        private void DestroyConnection(IPooledConnection conn)
        {
            DecrementPoolSize();
            if (conn == null)
            {
                return;
            }

            _poolMetricsListener?.ConnectionClosing();
            try
            {
                conn.Destroy();
            }
            finally
            {
                _poolMetricsListener?.ConnectionClosed();
            }
        }

        private async Task DestroyConnectionAsync(IPooledConnection conn)
        {
            DecrementPoolSize();
            if (conn == null)
            {
                return;
            }

            _poolMetricsListener?.ConnectionClosing();
            try
            {
                await conn.DestroyAsync().ConfigureAwait(false);
            }
            finally
            {
                _poolMetricsListener?.ConnectionClosed();
            }
        }

        /// <summary>
        /// Returns true if pool size is successfully increased, otherwise false.
        /// The reason to failed to increase the pool size probably due to the pool is full already
        /// </summary>
        /// <returns>true if pool size is successfully increased, otherwise false.</returns>
        private bool TryIncrementPoolSize()
        {
            if (_maxPoolSize != Config.Infinite && PoolSize >= _maxPoolSize)
            {
                return false;
            }

            Interlocked.Increment(ref _poolSize);

            return true;
        }

        private void DecrementPoolSize()
        {
            Interlocked.Decrement(ref _poolSize);
        }

        public IConnection Acquire(AccessMode mode)
        {
            var acquireEvent = new SimpleTimerEvent();
            _poolMetricsListener?.PoolAcquiring(acquireEvent);
            try
            {
                var conn = Acquire();
                _poolMetricsListener?.PoolAcquired(acquireEvent);
                return conn;
            }
            catch
            {
                _poolMetricsListener?.PoolFailedToAcquire();
                throw;
            }
        }

        public IPooledConnection Acquire()
        {
            using (var timeOutTokenSource = new CancellationTokenSource(_connAcquisitionTimeout))
            {
                return Acquire(timeOutTokenSource.Token);
            }
        }

        private IPooledConnection Acquire(CancellationToken cancellationToken)
        {
            return TryExecute(() =>
            {
                IPooledConnection connection = null;

                try
                {
                    while (true)
                    {
                        if (IsClosed)
                        {
                            ThrowObjectDisposedException();
                        }
                        else if (IsInactive)
                        {
                            ThrowClientExceptionDueToZombified();
                        }

                        if (!_idleConnections.TryTake(out connection))
                        {
                            do
                            {
                                if (!IsConnectionPoolFull())
                                {
                                    connection = CreateNewPooledConnection();
                                    if (connection != null)
                                    {
                                        break;
                                    }
                                }

                                if (_idleConnections.TryTake(out connection, SpinningWaitInterval, cancellationToken))
                                {
                                    break;
                                }
                            } while (!cancellationToken.IsCancellationRequested);

                            if (connection == null)
                            {
                                ThrowConnectionAcquisitionTimedOutException();
                            }
                        }

                        if (!_connectionValidator.OnRequire(connection))
                        {
                            DestroyConnection(connection);
                        }
                        else
                        {
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    _inUseConnections.TryAdd(connection);
                    if (IsClosed)
                    {
                        if (_inUseConnections.TryRemove(connection))
                        {
                            DestroyConnection(connection);
                        }

                        ThrowObjectDisposedException();
                    }
                }
                catch (OperationCanceledException ex)
                {
                    ThrowConnectionAcquisitionTimedOutException(ex);
                }

                return connection;
            });
        }

        private void ThrowConnectionAcquisitionTimedOutException(OperationCanceledException ex=null)
        {
            _poolMetricsListener?.PoolTimedOutToAcquire();
            throw new ClientException(
                $"Failed to obtain a connection from pool within {_connAcquisitionTimeout}", ex);
        }

        public Task<IConnection> AcquireAsync(AccessMode mode)
        {
            var acquireEvent = new SimpleTimerEvent();
            _poolMetricsListener?.PoolAcquiring(acquireEvent);
            var timeOutTokenSource = new CancellationTokenSource(_connAcquisitionTimeout);
            var task = AcquireAsync(timeOutTokenSource.Token).ContinueWith(t =>
            {
                timeOutTokenSource.Dispose();
                if (t.Status == TaskStatus.RanToCompletion)
                {
                    _poolMetricsListener?.PoolAcquired(acquireEvent);
                }
                else
                {
                    _poolMetricsListener?.PoolFailedToAcquire();
                }
                return t;
            }, TaskContinuationOptions.ExecuteSynchronously).Unwrap();
            return task;
        }

        private Task<IConnection> AcquireAsync(CancellationToken cancellationToken)
        {
            return TryExecuteAsync(async () =>
            {
                IPooledConnection connection = null;
                try
                {
                    while (true)
                    {
                        if (IsClosed)
                        {
                            ThrowObjectDisposedException();
                        }
                        else if (IsInactive)
                        {
                            ThrowClientExceptionDueToZombified();
                        }

                        if (!_idleConnections.TryTake(out connection))
                        {
                            do
                            {
                                if (!IsConnectionPoolFull())
                                {
                                    connection = await CreateNewPooledConnectionAsync().ConfigureAwait(false);
                                    if (connection != null)
                                    {
                                        break;
                                    }
                                }

                                await Task.Delay(SpinningWaitInterval, cancellationToken).ConfigureAwait(false);

                                if (_idleConnections.TryTake(out connection))
                                {
                                    break;
                                }
                            } while (!cancellationToken.IsCancellationRequested);

                            if (connection == null)
                            {
                                ThrowConnectionAcquisitionTimedOutException();
                            }
                        }

                        if (!_connectionValidator.OnRequire(connection))
                        {
                            await DestroyConnectionAsync(connection).ConfigureAwait(false);
                        }
                        else
                        {
                            break;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    _inUseConnections.TryAdd(connection);
                    if (IsClosed)
                    {
                        if (_inUseConnections.TryRemove(connection))
                        {
                            await DestroyConnectionAsync(connection).ConfigureAwait(false);
                        }
                        ThrowObjectDisposedException();
                    }
                }
                catch (OperationCanceledException ex)
                {
                    ThrowConnectionAcquisitionTimedOutException(ex);
                }

                return (IConnection) connection;
            });
        }

        private bool IsConnectionPoolFull()
        {
            return _maxPoolSize != Config.Infinite && _poolSize >= _maxPoolSize;
        }

        private bool IsIdlePoolFull()
        {
            return _maxIdlePoolSize != Config.Infinite && _idleConnections.Count >= _maxIdlePoolSize;
        }

        public void Release(IPooledConnection connection)
        {
            TryExecute(() =>
            {
                if (IsClosed)
                {
                    // pool already disposed, and this connection is also already closed
                    return;
                }

                // Remove from inUse
                if (!_inUseConnections.TryRemove(connection))
                {
                    // pool already disposed.
                    return;
                }

                if (!_connectionValidator.OnRelease(connection) || IsIdlePoolFull() || IsInactiveOrClosed)
                {
                    // cannot return to the idle pool
                    DestroyConnection(connection);
                    return;
                }

                // Add back to the idle pool
                _idleConnections.Add(connection);
                // Just dequeue any one connection and close it will ensure that all connections in the pool will finally be closed
                if (IsInactiveOrClosed && _idleConnections.TryTake(out connection))
                {
                    DestroyConnection(connection);
                }
            });
        }

        public Task ReleaseAsync(IPooledConnection connection)
        {
            return TryExecuteAsync(async () =>
            {
                if (IsClosed)
                {
                    // pool already disposed
                    return;
                }
                // Remove from idle
                if (!_inUseConnections.TryRemove(connection))
                {
                    // pool already disposed
                    return;
                }

                if (!await _connectionValidator.OnReleaseAsync(connection).ConfigureAwait(false)
                    || IsIdlePoolFull() || IsInactiveOrClosed)
                {
                    // cannot return to idle pool
                    await DestroyConnectionAsync(connection).ConfigureAwait(false);
                    return;
                }

                // Add back to idle pool
                _idleConnections.Add(connection);
                // Just dequeue any one connection and close it will ensure that all connections in the pool will finally be closed
                if (IsInactiveOrClosed && _idleConnections.TryTake(out connection))
                {
                    await DestroyConnectionAsync(connection).ConfigureAwait(false);
                }
            });
        }

        // For concurrent calling: you are free to get something from inUseConn or availConn when we dispose.
        // However it is forbidden to put something back to the conn queues after we've already started disposing.
        protected override void Dispose(bool disposing)
        {
            if (IsClosed)
                return;

            if (disposing)
            {
                Close();
            }

            base.Dispose(disposing);
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _poolStatus, Closed) != Closed)
            {
                TryExecute(() =>
                {
                    foreach (var inUseConnection in _inUseConnections)
                    {
                        Logger?.Info($"Disposing In Use Connection {inUseConnection.Id}");
                        if (_inUseConnections.TryRemove(inUseConnection))
                        {
                            DestroyConnection(inUseConnection);
                        }
                    }

                    TerminateIdleConnections();
                });
            }
        }

        public Task CloseAsync()
        {
            if (Interlocked.Exchange(ref _poolStatus, Closed) != Closed)
            {
                var allCloseTasks = new List<Task>();

                foreach (var inUseConnection in _inUseConnections)
                {
                    Logger?.Info($"Disposing In Use Connection {inUseConnection.Id}");
                    if (_inUseConnections.TryRemove(inUseConnection))
                    {
                        allCloseTasks.Add(DestroyConnectionAsync(inUseConnection));
                    }
                }

                allCloseTasks.AddRange(TerminateIdleConnectionsAsync());

                return Task.WhenAll(allCloseTasks);
            }

            return TaskHelper.GetCompletedTask();
        }

        public void Deactivate()
        {
            if (Interlocked.CompareExchange(ref _poolStatus, Inactive, Active) == Active)
            {
                TerminateIdleConnections();
            }
        }

        public Task DeactivateAsync()
        {
            if (Interlocked.CompareExchange(ref _poolStatus, Inactive, Active) == Active)
            {
                return Task.WhenAll(TerminateIdleConnectionsAsync());
            }
            return TaskHelper.GetCompletedTask();
        }

        public void Activate()
        {
            Interlocked.CompareExchange(ref _poolStatus, Active, Inactive);
        }

        private void TerminateIdleConnections()
        {
            while (_idleConnections.TryTake(out var connection))
            {
                Logger?.Debug($"Disposing Available Connection {connection.Id}");
                DestroyConnection(connection);
            }
        }

        private IEnumerable<Task> TerminateIdleConnectionsAsync()
        {
            var allCloseTasks = new List<Task>();
            while (_idleConnections.TryTake(out var connection))
            {
                Logger?.Debug($"Disposing Available Connection {connection.Id}");
                allCloseTasks.Add(DestroyConnectionAsync(connection));
            }
            return allCloseTasks;
        }

        private void ThrowObjectDisposedException()
        {
            FailedToAcquireConnectionDueToPoolClosed(this);
        }

        private void ThrowClientExceptionDueToZombified()
        {
            throw new ClientException(
                $"Failed to acquire a connection from connection pool for server with URI `{_uri}` " +
                "as this server has already been removed from routing table. " +
                "Please retry your statement again and you should be routed with a different server from the new routing table. " +
                "You should not see this error persistently.");
        }

        private static ConnectionPoolStatus AtomicRead(ref ConnectionPoolStatus value)
        {
            // change to the same value,
            // a.k.a. do nothing but return the original value
            return Interlocked.CompareExchange(ref value, Active, Active);
        }
        
        public override string ToString()
        {
            return $"{nameof(_idleConnections)}: {{{_idleConnections.ToContentString()}}}, " +
                   $"{nameof(_inUseConnections)}: {{{_inUseConnections}}}";
        }
    }
}
