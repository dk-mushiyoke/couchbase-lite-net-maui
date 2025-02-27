//  CouchbaseLiteErrorMessage.cs
//
//  Copyright (c) 2019 Couchbase, Inc All rights reserved.
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
using System.Collections.Generic;
using System.Text;

namespace Couchbase.Lite
{
    internal static partial class CouchbaseLiteErrorMessage
    {
        internal const string CreateDBDirectoryFailed = "Unable to create database directory.";
        internal const string CloseDBFailedReplications = "Cannot close the database. Please stop all of the replicators before closing the database.";
        internal const string CloseDBFailedQueryListeners = "Cannot close the database. Please remove all of the query listeners before closing the database.";
        internal const string DeleteDBFailedReplications = "Cannot delete the database. Please stop all of the replicators before closing the database.";
        internal const string DeleteDBFailedQueryListeners = "Cannot delete the database. Please remove all of the query listeners before closing the database.";
        internal const string DeleteDocFailedNotSaved = "Cannot delete a document that has not yet been saved.";
        internal const string DocumentNotFound = "The document doesn't exist in the database.";
        internal const string DocumentAnotherDatabase = "Cannot operate on a document from another database.";
        internal const string BlobDifferentDatabase = "A document contains a blob that was saved to a different database. The save operation cannot complete.";
        internal const string BlobContentNull = "No data available to write for install. Please ensure that all blobs in the document have non-null content.";
        internal const string ResolvedDocContainsNull = "Resolved document has a null body.";
        internal const string ResolvedDocFailedLiteCore = "LiteCore failed resolving conflict.";
        internal const string ResolvedDocWrongDb = "Resolved document's database {0} is different from expected database {1}.";
        internal const string DBClosedOrCollectionDeleted = "Attempt to perform an operation on a closed database or a deleted collection.";
        internal const string DBClosed = "Attempt to perform an operation on a closed database.";
        internal const string CollectionNotAvailable = "Attempt to perform an operation on an unavailable collection {0}.";
        internal const string NoDocumentRevision = "No revision data on the document!";
        internal const string FragmentPathNotExist = "Specified fragment path does not exist in object; cannot set value.";
        internal const string InvalidCouchbaseObjType = "{0} is not a valid type. Valid types are simple types and dictionaries and one-dimensional arrays of those types, including {1}.";
        internal const string InvalidValueToBeDeserialized = "Non-string or null key in data to be deserialized.";
        internal const string BlobContainsNoData = "Blob has no data available.";
        internal const string NotFileBasedURL = "{0} must be a file-based URL.";
        internal const string BlobReadStreamNotOpen = "Stream is not open.";
        internal const string CannotSetLogLevel = "Cannot set logging level without a configuration.";
        internal const string InvalidSchemeURLEndpoint = "Invalid scheme for URLEndpoint url ({0}). It must be either 'ws:' or 'wss:'.";
        internal const string InvalidEmbeddedCredentialsInURL = "Embedded credentials in a URL (username:password@url) are not allowed. Use the BasicAuthenticator class instead.";
        internal const string ReplicatorNotStopped = "Replicator is not stopped. Resetting checkpoint is only allowed when the replicator is in the stopped state.";
        internal const string QueryParamNotAllowedContainCollections = "Query parameters are not allowed to contain collections.";
        internal const string MissASforJoin = "Missing AS clause for JOIN.";
        internal const string MissONforJoin = "Missing ON statement for JOIN.";
        internal const string ExpressionsMustBeIExpressionOrString = "Expressions must either be {0} or String.";
        internal const string InvalidExpressionValueBetween = "Invalid expression value for expression of Between({0}).";
        internal const string ResultSetAlreadyEnumerated = "This result set has already been enumerated. Please re-execute the original query.";
        internal const string ExpressionsMustContainOnePlusElement = "{0} expressions must contain at least one element.";
        internal const string DuplicateSelectResultName = "Duplicate select result named {0}.";
        internal const string NoAliasInJoin = "The default database must have an alias in order to use a JOIN statement (Make sure your data source uses the As() function).";
        internal const string InvalidQueryDBNull = "Invalid query: The database is null.";
        internal const string InvalidQueryMissingSelectOrFrom = "Invalid query: missing Select or From.";
        internal const string NoDocEditInReplicationFilter = "Documents from a replication filter cannot be edited.";
        internal const string PullOnlyPendingDocIDs = "Pending Document IDs are not supported on pull-only replicators.";
        internal const string ReadOnlyObject = "This configuration object is readonly.";
        internal const string IdentityNotFound = "The identity is not present in the {0}";
        internal const string FailToConvertC4Cert = "Couldn't convert from C4Cert to {0} Array: {1}";
        internal const string DuplicateCertificate = "Certificate already exists with the label";
        internal const string MissingCommonName = "The Common Name attribute is required";
        internal const string FailToRemoveKeyPair = "Couldn't remove a keypair with error: {0}";
        internal const string InvalidHeartbeatInterval = "Heartbeat Interval cannot be less or equal to 0 full seconds.";
        internal const string InvalidMaxAttemptsInterval = "Max Attempts Interval cannot be less or equal to 0 full seconds.";
        internal const string InvalidMaxAttempts = "Max Attempts cannot be negative value.";
        internal const string InvalidJSONDictionaryForBlob = "Invalid JSON Dictionary represents in the Blob.";
        internal const string MissingDigestDueToBlobIsNotSavedToDB = "Missing Digest Due To Blob Is Not Saved To Database yet.";
        internal const string InvalidJSON = "Invalid JSON Value.";
        internal const string BlobDbNull = "Cannot access content from the blob that contains only metadata and has no database associated with it. To access the content, save the document first.";
    }
}

