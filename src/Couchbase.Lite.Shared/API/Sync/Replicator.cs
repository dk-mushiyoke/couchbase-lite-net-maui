// 
//  Replicator.cs
// 
//  Copyright (c) 2017 Couchbase, Inc All rights reserved.
// 
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
// 
//  http://www.apache.org/licenses/LICENSE-2.0
// 
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
// 

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;

using Couchbase.Lite.DI;
using Couchbase.Lite.Internal.Logging;
using Couchbase.Lite.Logging;
using Couchbase.Lite.Support;
using Couchbase.Lite.Util;

using JetBrains.Annotations;
using LiteCore;
using LiteCore.Interop;
using LiteCore.Util;
using Debug = System.Diagnostics.Debug;

namespace Couchbase.Lite.Sync
{
    /// <summary>
    /// An object that is responsible for the replication of data between two
    /// endpoints.  The replication can set up to be pull only, push only, or both
    /// (i.e. pusher and puller are no longer separate) between a database and a URL
    /// or a database and another database on the same filesystem.
    /// </summary>
    public sealed unsafe class Replicator : IDisposable, IStoppable, IChangeObservable<ReplicatorStatusChangedEventArgs>,
        IDocumentReplicatedObservable
    {
        #region Constants

        private const string Tag = nameof(Replicator);

        [NotNull]
        private static readonly C4ReplicatorMode[] Modes = {
            C4ReplicatorMode.Disabled, C4ReplicatorMode.Disabled, C4ReplicatorMode.OneShot, C4ReplicatorMode.Continuous
        };

        #endregion

        #region Variables

        [NotNull]private readonly ThreadSafety _databaseThreadSafety;

        [NotNull]private readonly Event<DocumentReplicationEventArgs> _documentEndedUpdate =
            new Event<DocumentReplicationEventArgs>();

        [NotNull]private readonly Event<ReplicatorStatusChangedEventArgs> _statusChanged =
            new Event<ReplicatorStatusChangedEventArgs>();

        private string _desc;
        private bool _disposed;

        private ReplicatorParameters _nativeParams;
        private C4ReplicatorStatus _rawStatus;
        private IReachability _reachability;
        private C4Replicator* _repl;
        private ConcurrentDictionary<Task, int> _conflictTasks = new ConcurrentDictionary<Task, int>();
        private IImmutableSet<string> _pendingDocIds;
        private ReplicatorConfiguration _config;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the configuration that was used to create this Replicator
        /// </summary>
        /// <exception cref="CouchbaseLiteException">Thrown if the replicator configuration doesn't contain any collection.</exception>
        [NotNull]
        public ReplicatorConfiguration Config => _config.Collections.Count > 0 ? _config 
            : throw new CouchbaseLiteException(C4ErrorCode.InvalidParameter, "Cannot operate on the replicator configuration without any collection.");

        /// <summary>
        /// Gets the current status of the <see cref="Replicator"/>
        /// </summary>
        public ReplicatorStatus Status { get; set; }

        internal SerialQueue DispatchQueue { get; } = new SerialQueue();

        /// <summary>
        /// This property allows the developer to know what the current server certificate is when using TLS communication. 
        /// The developer could save the certificate and pin the certificate next time when setting up the replicator to 
        /// provide an SSH type of authentication.
        /// </summary>
        public X509Certificate2 ServerCertificate { get; private set; }

        private ReplicationCollection[] replicationCollections => new ReplicationCollection[Config.Collections.Count];

        #endregion

        #region Constructors

        static Replicator()
        {
            WebSocketTransport.RegisterWithC4();
        }

        /// <summary>
        /// Constructs a replicator based on the given <see cref="ReplicatorConfiguration"/>
        /// </summary>
        /// <param name="config">The configuration to use to create the replicator</param>
        public Replicator([NotNull]ReplicatorConfiguration config)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(config), config);
            if (config.Collections.Count <= 0)
                throw new CouchbaseLiteException(C4ErrorCode.InvalidParameter, "Replicator Configuration must contain at least one collection.");

            _config = config.Freeze();
            _databaseThreadSafety = Config.Database.ThreadSafety;
        }

        /// <summary>
        /// Finalizer
        /// </summary>
        ~Replicator()
        {
            Dispose(true);
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Adds a change listener on this replication object (similar to a C# event)
        /// </summary>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        public ListenerToken AddChangeListener([NotNull]EventHandler<ReplicatorStatusChangedEventArgs> handler)
        {
            return AddChangeListener(null, handler);
        }

        /// <summary>
        /// Adds a change listener on this replication object (similar to a C# event, but
        /// with the ability to specify a <see cref="TaskScheduler"/> to schedule the 
        /// handler to run on)
        /// </summary>
        /// <param name="scheduler">The <see cref="TaskScheduler"/> to run the <c>handler</c> on
        /// (<c>null</c> for default)</param>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        public ListenerToken AddChangeListener([CanBeNull]TaskScheduler scheduler,
            [NotNull]EventHandler<ReplicatorStatusChangedEventArgs> handler)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(handler), handler);

            var cbHandler = new CouchbaseEventHandler<ReplicatorStatusChangedEventArgs>(handler, scheduler);
            _statusChanged.Add(cbHandler);
            return new ListenerToken(cbHandler, ListenerTokenType.Replicator, this);
        }

        /// <summary>
        /// Adds a documents ended listener on this replication object (similar to a C# event)
        /// </summary>
        /// <remarks>
        /// Make sure add documents ended listener on this replication object before starting the replicator.
        /// </remarks>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        public ListenerToken AddDocumentReplicationListener([NotNull]EventHandler<DocumentReplicationEventArgs> handler)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(handler), handler);

            return AddDocumentReplicationListener(null, handler);
        }

        /// <summary>
        /// Adds a document ended listener on this replication object (similar to a C# event, but
        /// with the ability to specify a <see cref="TaskScheduler"/> to schedule the 
        /// handler to run on)
        /// </summary>
        /// <remarks>
        /// Make sure add documents ended listener on this replication object before starting the replicator.
        /// </remarks>
        /// <param name="scheduler">The <see cref="TaskScheduler"/> to run the <c>handler</c> on
        /// (<c>null</c> for default)</param>
        /// <param name="handler">The logic to run during the callback</param>
        /// <returns>A token to remove the handler later</returns>
        public ListenerToken AddDocumentReplicationListener([CanBeNull]TaskScheduler scheduler,
            [NotNull]EventHandler<DocumentReplicationEventArgs> handler)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(handler), handler);
            var cbHandler = new CouchbaseEventHandler<DocumentReplicationEventArgs>(handler, scheduler);
            if (_documentEndedUpdate.Add(cbHandler) == 0) {
                SetProgressLevel(C4ReplicatorProgressLevel.ReplProgressPerDocument);
            }
            
            return new ListenerToken(cbHandler, ListenerTokenType.DocReplicated, this);
        }

        /// <summary>
        /// Removes a previously added change listener via its <see cref="ListenerToken"/> and/or
        /// Removes a previously added documents ended listener via its <see cref="ListenerToken"/>
        /// </summary>
        /// <param name="token">The token received from <see cref="AddChangeListener(TaskScheduler, EventHandler{ReplicatorStatusChangedEventArgs})"/>
        /// and/or The token received from <see cref="AddDocumentReplicationListener(TaskScheduler, EventHandler{DocumentReplicationEventArgs})"/></param>
        public void RemoveChangeListener(ListenerToken token)
        {
            if (token.Type == ListenerTokenType.Replicator) {
                _statusChanged.Remove(token);
            } else if (_documentEndedUpdate.Remove(token) == 0) {
                SetProgressLevel(C4ReplicatorProgressLevel.ReplProgressOverall);
            }
        }

        /// <summary>
        /// Starts the replication
        /// </summary>
        public void Start()
        {
            Start(false);
        }

        /// <summary>
        /// Starts the replication with an option to reset the checkpoint.
        /// </summary>
        /// <param name="reset">Resets the local checkpoint of the replicator, meaning that it will read all changes since the beginning
        /// of time from the remote database.
        /// </param>
        public void Start(bool reset)
        {
            var status = default(C4ReplicatorStatus);
            DispatchQueue.DispatchSync(() =>
            {
                if (_disposed) {
                    throw new ObjectDisposedException(CouchbaseLiteErrorMessage.ReplicatorDisposed);
                }

                var err = SetupC4Replicator();
                if (err.code > 0) {
                    WriteLog.To.Sync.E(Tag, $"Setup replicator {this} failed.");
                }

                if (_repl != null) {
                    status = Native.c4repl_getStatus(_repl);
                    if (status.level == C4ReplicatorActivityLevel.Stopped
                    || status.level == C4ReplicatorActivityLevel.Stopping
                    || status.level == C4ReplicatorActivityLevel.Offline) {
                        ServerCertificate = null;
                        WriteLog.To.Sync.I(Tag, $"{this}: Starting");
                        Native.c4repl_start(_repl, Config.Options.Reset || reset);
                        Config.Options.Reset = false;
                        Config.Database.AddActiveStoppable(this);
                        status = Native.c4repl_getStatus(_repl);
                    }
                } else {
                    status = new C4ReplicatorStatus {
                        error = err,
                        level = C4ReplicatorActivityLevel.Stopped,
                        progress = new C4Progress()
                    };
                }
            });

            UpdateStateProperties(status);
            DispatchQueue.DispatchSync(() => StatusChangedCallback(status));
        }


        /// <summary>
        /// Stops a running replicator.  This method returns immediately; when the replicator actually
        /// stops, the replicator will change its status's activity level to <see cref="ReplicatorActivityLevel.Stopped"/>
        /// and the replicator change notification will be notified accordingly.
        /// </summary>
        public void Stop()
        {
            DispatchQueue.DispatchSync(() =>
            {
                StopReachabilityObserver();
                if (_repl != null) {
                    if (_rawStatus.level == C4ReplicatorActivityLevel.Stopped
                        || _rawStatus.level == C4ReplicatorActivityLevel.Stopping) {
                        return;
                    }

                    Native.c4repl_stop(_repl);
                }
            });
        }

        /// <summary>
        /// [DEPRECATED] Gets a list of document IDs that are going to be pushed, but have not been pushed yet
        /// <item type="bullet">
        /// <description>API is a snapshot and results may change between the time the call was made and the time</description>
        /// </item>
        /// </summary>
        /// <returns>An immutable set of strings, each of which is a document ID</returns>
        /// <exception cref="CouchbaseLiteException">Thrown if no push replication</exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        [Obsolete("GetPendingDocumentIDs() is deprecated, please use GetPendingDocumentIDs(Collection collection)")]
        [NotNull]
        public IImmutableSet<string> GetPendingDocumentIDs()
        {
            return GetPendingDocumentIDs(Config.Database.DefaultCollection);
        }

        /// <summary>
        /// [DEPRECATED] Checks whether or not a document with the given ID has any pending revisions to push
        /// </summary>
        /// <param name="documentID">The document ID</param>
        /// <returns>A bool which represents whether or not the document with the corresponding ID has one or more pending revisions.  
        /// <c>true</c> means that one or more revisions have not been pushed to the remote yet, 
        /// and <c>false</c> means that all revisions on the document have been pushed</returns>
        /// <exception cref="CouchbaseLiteException">Thrown if no push replication</exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        [Obsolete("IsDocumentPending(string documentID) is deprecated, please use IsDocumentPending(string documentID, Collection collection)")]
        public bool IsDocumentPending([NotNull]string documentID)
        {
            return IsDocumentPending(documentID, Config.Database.DefaultCollection);
        }

        /// <summary>
        /// Checks whether or not a document with the given ID in the given collection is pending to push or not. 
        /// If the given collection is not part of the replication, an Invalid Parameter Exception will be thrown.
        /// </summary>
        /// <param name="documentID">The document ID</param>
        /// <param name="collection">The collection contains the doc with the given document ID</param>
        /// <returns>A bool which represents whether or not the document with the corresponding ID has one or more pending revisions.  
        /// <c>true</c> means that one or more revisions have not been pushed to the remote yet, 
        /// and <c>false</c> means that all revisions on the document have been pushed</returns>
        /// <exception cref="CouchbaseLiteException">Thrown if no push replication</exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        public bool IsDocumentPending([NotNull] string documentID, [NotNull] Collection collection)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(documentID), documentID);
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(collection), collection);
            bool isDocPending = false;

            DispatchQueue.DispatchSync(() => {
                var errSetupRepl = SetupC4Replicator();
                if (errSetupRepl.code > 0) {
                    CBDebug.LogAndThrow(WriteLog.To.Sync, CouchbaseException.Create(errSetupRepl), Tag, errSetupRepl.ToString(), true);
                }

                if (!IsPushing(collection)) {
                    CBDebug.LogAndThrow(WriteLog.To.Sync,
                        new CouchbaseLiteException(C4ErrorCode.Unsupported, CouchbaseLiteErrorMessage.PullOnlyPendingDocIDs),
                        Tag, CouchbaseLiteErrorMessage.PullOnlyPendingDocIDs, true);
                }
            });

            using (var collName_ = new C4String(collection.Name))
            using (var scopeName_ = new C4String(collection.Scope.Name)) {
                var collectionSpec = new C4CollectionSpec()
                {
                    name = collName_.AsFLSlice(),
                    scope = scopeName_.AsFLSlice()
                };

                LiteCoreBridge.Check(err =>
                {
                    isDocPending = Native.c4repl_isDocumentPending(_repl, documentID, collectionSpec, err);
                    return isDocPending;
                });
            }

            return isDocPending;
        }

        /// <summary>
        /// Gets a list of document IDs of docs in the given collection that are going to be pushed, but have not been pushed yet. 
        /// If the given collection is not part of the replication, an Invalid Parameter Exception will be thrown.
        /// <item type="bullet">
        /// <description>API is a snapshot and results may change between the time the call was made and the time</description>
        /// </item>
        /// </summary>
        /// <param name="collection">The collection contains the list of document IDs of docs</param>
        /// <returns>An immutable set of strings, each of which is a document ID</returns>
        /// <exception cref="CouchbaseLiteException">Thrown if no push replication</exception>
        /// <exception cref="CouchbaseException">Thrown if an error condition is returned from LiteCore</exception>
        [NotNull]
        public IImmutableSet<string> GetPendingDocumentIDs([NotNull] Collection collection)
        {
            CBDebug.MustNotBeNull(WriteLog.To.Sync, Tag, nameof(collection), collection);
            var result = new HashSet<string>();
            byte[] pendingDocIds = null;

            DispatchQueue.DispatchSync(() => {
                var errSetupRepl = SetupC4Replicator();
                if (errSetupRepl.code > 0) {
                    CBDebug.LogAndThrow(WriteLog.To.Sync, CouchbaseException.Create(errSetupRepl), Tag, errSetupRepl.ToString(), true);
                }

                if (!IsPushing(collection)) {
                    CBDebug.LogAndThrow(WriteLog.To.Sync,
                        new CouchbaseLiteException(C4ErrorCode.Unsupported, CouchbaseLiteErrorMessage.PullOnlyPendingDocIDs),
                        Tag, CouchbaseLiteErrorMessage.PullOnlyPendingDocIDs, true);
                }
            });

            using (var collName_ = new C4String(collection.Name))
            using (var scopeName_ = new C4String(collection.Scope.Name)) {
                var collectionSpec = new C4CollectionSpec()
                {
                    name = collName_.AsFLSlice(),
                    scope = scopeName_.AsFLSlice()
                };

                pendingDocIds = LiteCoreBridge.Check(err =>
                {
                    return Native.c4repl_getPendingDocIDs(_repl, collectionSpec, err);
                });

                if (pendingDocIds != null) {
                    _databaseThreadSafety.DoLocked(() =>
                    {
                        var flval = Native.FLValue_FromData(pendingDocIds, FLTrust.Trusted);
                        var flarr = Native.FLValue_AsArray(flval);
                        var cnt = (int)Native.FLArray_Count(flarr);
                        for (int i = 0; i < cnt; i++) {
                            var flv = Native.FLArray_Get(flarr, (uint)i);
                            result.Add(Native.FLValue_AsString(flv));
                        }

                        Array.Clear(pendingDocIds, 0, pendingDocIds.Length);
                        pendingDocIds = null;
                    });
                }
            }

            _pendingDocIds = result.ToImmutableHashSet<string>();
            return _pendingDocIds;
        }

        #endregion

        #region Internal Methods

        internal void WatchForCertificate(WebSocketWrapper wrapper)
        {
            wrapper.PeerCertificateReceived += OnTlsCertificate;
        }

        internal void CheckForCookiesToSet(WebSocketWrapper wrapper)
        {
            wrapper.CookiesToSetReceived += OnCookiesToSetReceived;
        }

        #endregion

        #region Private Methods - Filters

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4ReplicatorValidationFunction))]
        #endif
        private static bool PullValidateCallback(C4CollectionSpec collectionSpec, FLSlice docID, FLSlice revID, C4RevisionFlags revisionFlags, FLDict* dict, void* context)
        {
            var replicator = GCHandle.FromIntPtr((IntPtr)context).Target as Replicator;
            if (replicator == null) {
                WriteLog.To.Database.E(Tag, "Pull filter context pointing to invalid object {0}, aborting and returning true...",
                    replicator);
                return true;
            }

            var docIDStr = docID.CreateString();
            if (docIDStr == null) {
                WriteLog.To.Database.E(Tag, "Null document ID received in pull filter, rejecting...");
                return false;
            }

            var collName = collectionSpec.name.CreateString();
            var scope = collectionSpec.scope.CreateString();
            var flags = revisionFlags.ToDocumentFlags();
            return replicator.PullValidateCallback(collName, scope, docIDStr, revID.CreateString(), dict, flags);
        }

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4ReplicatorValidationFunction))]
        #endif
        private static bool PushFilterCallback(C4CollectionSpec collectionSpec, FLSlice docID, FLSlice revID, C4RevisionFlags revisionFlags, FLDict* dict, void* context)
        {
            var replicator = GCHandle.FromIntPtr((IntPtr)context).Target as Replicator;
            if (replicator == null) {
                WriteLog.To.Database.E(Tag, "Push filter context pointing to invalid object {0}, aborting and returning true...",
                    replicator);
                return true;
            }

            var docIDStr = docID.CreateString();
            if (docIDStr == null) {
                WriteLog.To.Database.E(Tag, "Null document ID received in push filter, rejecting...");
                return false;
            }

            var collName = collectionSpec.name.CreateString();
            var scope = collectionSpec.scope.CreateString();
            var flags = revisionFlags.ToDocumentFlags();
            return replicator.PushFilterCallback(collName, scope, docIDStr, revID.CreateString(), dict, flags);
        }

        private bool PullValidateCallback(string collName, string scope, string docID, string revID, FLDict* value, DocumentFlags flags)
        {
            var coll = Config.Database.GetCollection(collName, scope);
            var config = Config.GetCollectionConfig(coll);
            return config.PullFilter(new Document(coll, docID, revID, value), flags);
        }

        private bool PushFilterCallback(string collName, string scope, string docID, string revID, FLDict* value, DocumentFlags flags)
        {
            var coll = Config.Database.GetCollection(collName, scope);
            var config = Config.GetCollectionConfig(coll);
            return config.PushFilter(new Document(coll, docID, revID, value), flags);
        }

        #endregion

        #region Private Methods - Doc Ended

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4ReplicatorDocumentEndedCallback))]
        #endif
        private static void OnDocEnded(C4Replicator* repl, bool pushing, IntPtr numDocs, C4DocumentEnded** docs, void* context)
        {
            if (docs == null || numDocs == IntPtr.Zero) {
                return;
            }

            var replicatedDocumentsContainConflict = new List<ReplicatedDocument>();
            var documentReplications = new List<ReplicatedDocument>();
            for (int i = 0; i < (int)numDocs; i++) {
                var current = docs[i];
                if (!pushing && current->error.domain == C4ErrorDomain.LiteCoreDomain &&
                    current->error.code == (int)C4ErrorCode.Conflict) {
                    replicatedDocumentsContainConflict.Add(new ReplicatedDocument(current->docID.CreateString() ?? "", current->collectionSpec,
                        current->flags, current->error, current->errorIsTransient));
                } else {
                    documentReplications.Add(new ReplicatedDocument(current->docID.CreateString() ?? "", current->collectionSpec,
                        current->flags, current->error, current->errorIsTransient));
                }
            }

            var replicator = GCHandle.FromIntPtr((IntPtr)context).Target as Replicator;
            if (documentReplications.Count > 0) {
                replicator?.DispatchQueue.DispatchAsync(() =>
                {
                    replicator.OnDocEnded(documentReplications, pushing);
                });
            }

            if (replicatedDocumentsContainConflict.Count > 0) {
                replicator?.DispatchQueue.DispatchAsync(() =>
                {
                    replicator.OnDocEndedWithConflict(replicatedDocumentsContainConflict);
                });
            }
        }

        private void OnDocEndedWithConflict(List<ReplicatedDocument> replications)
        {
            if (_disposed) {
                return;
            }

            for (int i = 0; i < replications.Count; i++) {
                var replication = replications[i];
                // Conflict pulling a document -- the revision was added but app needs to resolve it:
                var safeDocID = new SecureLogString(replication.Id, LogMessageSensitivity.PotentiallyInsecure);
                WriteLog.To.Sync.I(Tag, $"{this} pulled conflicting version of '{safeDocID}'");
                Task t = Task.Run(() =>
                {
                    try {
                        var coll = Config.Database.GetCollection(replication.CollectionName, replication.ScopeName);
                        var collectionConfig = Config.GetCollectionConfig(coll);
                        Config.Database.ResolveConflict(replication.Id, collectionConfig.ConflictResolver, coll);
                        replication = replication.ClearError();
                    } catch (CouchbaseException e) {
                        replication.Error = e;
                    } catch (Exception e) {
                        replication.Error = new CouchbaseLiteException(C4ErrorCode.UnexpectedError, e.Message, e);
                    }

                    if (replication.Error != null) {
                        WriteLog.To.Sync.W(Tag, $"Conflict resolution of '{replication.Id}' failed", replication.Error);
                    }

                    _documentEndedUpdate.Fire(this, new DocumentReplicationEventArgs(new[] { replication }, false));
                });

                _conflictTasks.TryAdd(t.ContinueWith(task => _conflictTasks.TryRemove(t, out var dummy)), 0);
            }
        }

        private void OnDocEnded(List<ReplicatedDocument> replications, bool pushing)
        {
            if (_disposed) {
                return;
            }

            for (int i = 0; i < replications.Count; i++) {
                var replication = replications[i];
                var error = replication.NativeError;
                if (error.code > 0) {
                    var docID = replication.Id;
                    var transient = replication.IsTransient;
                    var logDocID = new SecureLogString(docID, LogMessageSensitivity.PotentiallyInsecure);
                    var transientStr = transient ? "transient " : String.Empty;
                    var dirStr = pushing ? "pushing" : "pulling";
                    WriteLog.To.Sync.I(Tag,
                        $"{this}: {transientStr}error {dirStr} '{logDocID}' : {error.code} ({Native.c4error_getMessage(error)})");
                }
            }

            _documentEndedUpdate.Fire(this, new DocumentReplicationEventArgs(replications, pushing));
        }

        #endregion

        #region Private Methods - Status Change

        #if __IOS__
        [ObjCRuntime.MonoPInvokeCallback(typeof(C4ReplicatorStatusChangedCallback))]
        #endif
        private static void StatusChangedCallback(C4Replicator* repl, C4ReplicatorStatus status, void* context)
        {
            var replicator = GCHandle.FromIntPtr((IntPtr)context).Target as Replicator;
            if (replicator == null)
                return;

            replicator.WaitPendingConflictTasks(status);
            replicator.DispatchQueue.DispatchSync(() =>
            {
                replicator.StatusChangedCallback(status);
            });
        }

        // Must be called from within the ThreadSafety
        private void StatusChangedCallback(C4ReplicatorStatus status)
        {
            if (_disposed) {
                return;
            }

            // idle or busy
            if ((status.level > C4ReplicatorActivityLevel.Connecting
                && status.level != C4ReplicatorActivityLevel.Stopping)
                && status.error.code == 0) {
                StopReachabilityObserver();
            }

            UpdateStateProperties(status);

            // offline
            if (status.level == C4ReplicatorActivityLevel.Offline) {
                StartReachabilityObserver();
            }

            //  stopped
            if (status.level == C4ReplicatorActivityLevel.Stopped) {
                StopReachabilityObserver();
                Stopped();
            }

            try {
                _statusChanged.Fire(this, new ReplicatorStatusChangedEventArgs(Status));
            } catch (Exception e) {
                WriteLog.To.Sync.W(Tag, "Exception during StatusChanged callback", e);
            }
        }

        private void WaitPendingConflictTasks(C4ReplicatorStatus status)
        {
            if (status.error.code == 0 && status.error.domain == 0)
                return;

            if (status.level == C4ReplicatorActivityLevel.Stopped
                || status.level == C4ReplicatorActivityLevel.Idle) {
                var array = _conflictTasks?.Keys?.ToArray();
                if (array != null) {
                    Task.WaitAll(array);
                }
            }
        }

        #endregion

        #region Private Methods - Reachability

        private void StartReachabilityObserver()
        {
            if (_reachability != null) {
                return;
            }

            var remoteUrl = (Config.Target as URLEndpoint)?.Url;
            if (remoteUrl == null) {
                return;
            }

            _reachability = Service.GetInstance<IReachability>() ?? new Reachability();
            _reachability.StatusChanged += ReachabilityChanged;
            _reachability.Url = remoteUrl;
            _reachability.Start();
        }

        private void StopReachabilityObserver()
        {
            if (_reachability != null) {
                _reachability.StatusChanged -= ReachabilityChanged;
                _reachability.Stop();
                _reachability = null;
            }
        }

        private void ReachabilityChanged(object sender, NetworkReachabilityChangeEventArgs e)
        {
            Debug.Assert(e != null);

            DispatchQueue.DispatchAsync(() =>
            {
                if (_repl != null /* just to be safe */) {
                    Native.c4repl_setHostReachable(_repl, e.Status == NetworkReachabilityStatus.Reachable);
                }
            });
        }

        #endregion

        #region Private Methods

        private bool IsPushing(Collection collection)
        {
            var collConfig = Config.GetCollectionConfig(collection);
            return collConfig.ReplicatorType.HasFlag(ReplicatorType.Push);
        }

        private static C4ReplicatorMode Mkmode(bool active, bool continuous)
        {
            return Modes[2 * Convert.ToInt32(active) + Convert.ToInt32(continuous)];
        }

        private void OnTlsCertificate(object sender, TlsCertificateReceivedEventArgs e)
        {
            ((WebSocketWrapper) sender).PeerCertificateReceived -= OnTlsCertificate;
            ServerCertificate = e.PeerCertificate;
        }

        private void OnCookiesToSetReceived(object sender, string e)
        {
            ((WebSocketWrapper) sender).CookiesToSetReceived -= OnCookiesToSetReceived;

            var remoteUrl = (Config.Target as URLEndpoint)?.Url;
            if (remoteUrl == null) {
                return;
            }

            Config.Database.SaveCookie(e, remoteUrl);
        }

        private void Dispose(bool finalizing)
        {
            if(!finalizing) {
                DispatchQueue.DispatchSync(() =>
                {
                    if (_disposed) {
                        return;
                    }

                    foreach(var col in replicationCollections) {
                        if (col != null) {
                            GCHandle.FromIntPtr((IntPtr)col.C4ReplicationCol.callbackContext).Free();
                            col.Dispose();
                        }
                    }

                    _nativeParams?.Dispose();
                    Config.Options.Dispose();
                    if (Status.Activity != ReplicatorActivityLevel.Stopped) {
                        var newStatus = new ReplicatorStatus(ReplicatorActivityLevel.Stopped, Status.Progress, null);
                        _statusChanged.Fire(this, new ReplicatorStatusChangedEventArgs(newStatus));
                        Status = newStatus;
                    }

                    Stop();
                    Native.c4repl_free(_repl);
                    _repl = null;
                    _disposed = true;
                });
            } else {
                 Native.c4repl_free(_repl);
                 _repl = null;
            }
        }

        private C4Error SetupC4Replicator()
        {
            Config.Database.CheckOpenLocked();
            C4Error err = new C4Error();
            if (_repl != null) {
                Native.c4repl_setOptions(_repl, ((FLSlice) Config.Options.FLEncode()).ToArrayFast());
                return err;
            }

            _desc = ToString(); // Cache this; it may be called a lot when logging

            // Target:
            var addr = new C4Address();
            Database otherDB = null;
            var remoteUrl = Config.RemoteUrl;
            string dbNameStr = remoteUrl?.Segments?.Last().TrimEnd('/');
            using (var dbNameStr_ = new C4String(dbNameStr))
            using (var remoteUrlStr_ = new C4String(remoteUrl?.AbsoluteUri)) {
                FLSlice dn = dbNameStr_.AsFLSlice();
                C4Address localAddr;
                var addrFromUrl = NativeRaw.c4address_fromURL(remoteUrlStr_.AsFLSlice(), &localAddr, &dn);
                addr = localAddr;

                if (addrFromUrl) {
                    //get cookies from url and add to replicator options
                    var cookiestring = Config.Database.GetCookies(remoteUrl);
                    if (!String.IsNullOrEmpty(cookiestring)) {
                        var split = cookiestring.Split(';') ?? Enumerable.Empty<string>();
                        foreach (var entry in split) {
                            var pieces = entry?.Split('=');
                            if (pieces?.Length != 2) {
                                WriteLog.To.Sync.W(Tag, "Garbage cookie value, ignoring");
                                continue;
                            }

                            Config.Options.Cookies.Add(new Cookie(pieces[0]?.Trim(), pieces[1]?.Trim()));
                        }
                    }
                } else {
                    Config.OtherDB?.CheckOpenLocked();
                    otherDB = Config.OtherDB;
                }

                var options = Config.Options;

                Config.Authenticator?.Authenticate(options);

                options.Build();
                var push = Config.ReplicatorType.HasFlag(ReplicatorType.Push);
                var pull = Config.ReplicatorType.HasFlag(ReplicatorType.Pull);
                var continuous = Config.Continuous;

                var socketFactory = Config.SocketFactory;
                socketFactory.context = GCHandle.ToIntPtr(GCHandle.Alloc(this)).ToPointer();
                _nativeParams = new ReplicatorParameters(options)
                {
                    Context = this,
                    OnDocumentEnded = OnDocEnded,
                    OnStatusChanged = StatusChangedCallback,
                    SocketFactory = &socketFactory
                };

                // Clear the reset flag, it is a one-time thing
                options.Reset = false;

                var collCnt = (long)Config.Collections.Count;
                _nativeParams.CollectionCount = collCnt;
                DispatchQueue.DispatchSync(() =>
                {
                    C4ReplicationCollection[] c4ReplicationCollections = new C4ReplicationCollection[collCnt];
                    C4CollectionSpec[] c4CollectionSpec = new C4CollectionSpec[collCnt];
                    for (int i = 0; i < collCnt; i++) {
                        var collectionConfig = Config.CollectionConfigs.ElementAt(i);
                        var col = collectionConfig.Key;
                        var config = collectionConfig.Value;
                        var colConfigOptions = config.Options;

                        //TODO: in the future we can set different replicator type per collection
                        //var collPush = config.ReplicatorType.HasFlag(ReplicatorType.Push);
                        //var collPull = config.ReplicatorType.HasFlag(ReplicatorType.Pull);
                        //for now collecion config's ReplicatorType should be the same as ReplicatorType in replicator config
                        config.ReplicatorType = Config.ReplicatorType; 

                        colConfigOptions.Build();

                        var collName = new C4String(col.Name);
                        var scopeName = new C4String(col.Scope.Name);
                        c4CollectionSpec[i] = new C4CollectionSpec()
                        {
                            name = collName.AsFLSlice(),
                            scope = scopeName.AsFLSlice()
                        };

                        var replicationCollection = new ReplicationCollection(colConfigOptions)
                        {
                            Push = Mkmode(push, continuous),
                            Pull = Mkmode(pull, continuous),
                            Context = this
                        };

                        if (config.PushFilter != null)
                            replicationCollection.PushFilter = PushFilterCallback;
                        if (config.PullFilter != null)
                            replicationCollection.PullFilter = PullValidateCallback;

                        var localC4ReplicationCol = replicationCollection.C4ReplicationCol;
                        localC4ReplicationCol.collection = c4CollectionSpec[i];
                        c4ReplicationCollections[i] = localC4ReplicationCol;
                        replicationCollections[i] = replicationCollection;
                    }

                    C4Error localErr = new C4Error();
                    fixed (C4ReplicationCollection* ptr = c4ReplicationCollections) {
                        _nativeParams.ReplicationCollection = ptr;
                        #if COUCHBASE_ENTERPRISE
                        if (otherDB != null)
                            _repl = Native.c4repl_newLocal(Config.Database.c4db, otherDB.c4db, _nativeParams.C4Params,
                                &localErr);
                        else
                        #endif
                            _repl = Native.c4repl_new(Config.Database.c4db, addr, dbNameStr, _nativeParams.C4Params, &localErr);
                    }

                    if (_documentEndedUpdate.Counter > 0) {
                        SetProgressLevel(C4ReplicatorProgressLevel.ReplProgressPerDocument);
                    }

                    err = localErr;
                });
            }

            return err;
        }

        private void SetProgressLevel(C4ReplicatorProgressLevel progressLevel)
        {
            if (_repl == null) {
                WriteLog.To.Sync.V(Tag, $"Progress level {progressLevel} is not yet set because C4Replicator is not created.");
                return;
            }

            C4Error err = new C4Error();
            if (!Native.c4repl_setProgressLevel(_repl, progressLevel, &err) || err.code > 0) {
                WriteLog.To.Sync.W(Tag, $"Failed set progress level to {progressLevel}", err);
            }
        }

        private void Stopped()
        {
            Debug.Assert(_rawStatus.level == C4ReplicatorActivityLevel.Stopped);
            Config.Database.RemoveActiveStoppable(this);
        }

        private void UpdateStateProperties(C4ReplicatorStatus state)
        {
            Exception error = null;
            if (state.error.code > 0) {
                error = CouchbaseException.Create(state.error);
            }

            _rawStatus = state;
            var level = (ReplicatorActivityLevel) state.level;
            var progress = new ReplicatorProgress(state.progress.unitsCompleted, state.progress.unitsTotal);
            Status = new ReplicatorStatus(level, progress, error);
            WriteLog.To.Sync.I(Tag, $"{this} is {state.level}, progress {state.progress.unitsCompleted}/{state.progress.unitsTotal}");
        }

        #endregion

        #region Overrides

        /// <inheritdoc />
        public override string ToString()
        {
            if (_desc != null) {
                return _desc;
            }

            var sb = new StringBuilder(3, 3);
            if (Config.ReplicatorType.HasFlag(ReplicatorType.Pull)) {
                sb.Append("<");
            }

            if (Config.Continuous) {
                sb.Append("*");
            }

            if (Config.ReplicatorType.HasFlag(ReplicatorType.Push)) {
                sb.Append(">");
            }

            return $"{GetType().Name}[{sb} {Config.Target}]";
        }

        #endregion

        #region IDisposable

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        #endregion
    }
}