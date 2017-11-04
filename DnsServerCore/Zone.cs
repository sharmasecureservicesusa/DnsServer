﻿/*
Technitium DNS Server
Copyright (C) 2017  Shreyas Zare (shreyas@technitium.com)

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using TechnitiumLibrary.Net.Dns;

namespace DnsServerCore
{
    public class Zone
    {
        #region variables

        const uint DEFAULT_RECORD_TTL = 60u;

        readonly bool _authoritativeZone;

        readonly Zone _parentZone;
        readonly string _zoneLabel;
        readonly string _zoneName;

        bool _disabled;

        readonly ConcurrentDictionary<string, Zone> _zones = new ConcurrentDictionary<string, Zone>();
        readonly ConcurrentDictionary<DnsResourceRecordType, DnsResourceRecord[]> _entries = new ConcurrentDictionary<DnsResourceRecordType, DnsResourceRecord[]>();

        #endregion

        #region constructor

        public Zone(bool authoritativeZone)
        {
            _authoritativeZone = authoritativeZone;
            _zoneName = "";

            if (!_authoritativeZone)
                LoadRootHintsInCache();
        }

        private Zone(Zone parentZone, string zoneLabel)
        {
            _authoritativeZone = parentZone._authoritativeZone;
            _parentZone = parentZone;
            _zoneLabel = zoneLabel;

            string zoneName = zoneLabel;

            if (_parentZone._zoneName != "")
                zoneName += "." + _parentZone._zoneName;

            _zoneName = zoneName;
        }

        #endregion

        #region private

        private void LoadRootHintsInCache()
        {
            List<DnsResourceRecord> nsRecords = new List<DnsResourceRecord>(13);

            foreach (NameServerAddress rootNameServer in DnsClient.ROOT_NAME_SERVERS_IPv4)
            {
                nsRecords.Add(new DnsResourceRecord("", DnsResourceRecordType.NS, DnsClass.IN, 172800, new DnsNSRecord(rootNameServer.Domain)));

                CreateZone(this, rootNameServer.Domain).SetRecords(DnsResourceRecordType.A, new DnsResourceRecord[] { new DnsResourceRecord(rootNameServer.Domain, DnsResourceRecordType.A, DnsClass.IN, 172800, new DnsARecord(rootNameServer.EndPoint.Address)) });
            }

            foreach (NameServerAddress rootNameServer in DnsClient.ROOT_NAME_SERVERS_IPv6)
            {
                CreateZone(this, rootNameServer.Domain).SetRecords(DnsResourceRecordType.AAAA, new DnsResourceRecord[] { new DnsResourceRecord(rootNameServer.Domain, DnsResourceRecordType.AAAA, DnsClass.IN, 172800, new DnsAAAARecord(rootNameServer.EndPoint.Address)) });
            }

            SetRecords(DnsResourceRecordType.NS, nsRecords.ToArray());
        }

        private static string[] ConvertDomainToPath(string domainName)
        {
            if (string.IsNullOrEmpty(domainName))
                return new string[] { };

            string[] path = domainName.ToLower().Split('.');
            Array.Reverse(path);

            return path;
        }

        private static Zone CreateZone(Zone rootZone, string domain)
        {
            Zone currentZone = rootZone;
            string[] path = ConvertDomainToPath(domain);

            for (int i = 0; i < path.Length; i++)
            {
                string nextZoneLabel = path[i];

                Zone nextZone = currentZone._zones.GetOrAdd(nextZoneLabel, delegate (string key)
                {
                    return new Zone(currentZone, nextZoneLabel);
                });

                currentZone = nextZone;
            }

            return currentZone;
        }

        private static Zone FindClosestZone(Zone rootZone, string domain)
        {
            Zone currentZone = rootZone;
            string[] path = ConvertDomainToPath(domain);

            for (int i = 0; i < path.Length; i++)
            {
                string nextZoneName = path[i];

                if (currentZone._zones.TryGetValue(nextZoneName, out Zone nextZone))
                    currentZone = nextZone;
                else
                    return currentZone;

                if (currentZone._disabled)
                    return currentZone;
            }

            return currentZone;
        }

        private static Zone GetZone(Zone rootZone, string domain)
        {
            Zone currentZone = rootZone;
            string[] path = ConvertDomainToPath(domain);

            for (int i = 0; i < path.Length; i++)
            {
                string nextZoneName = path[i];

                if (currentZone._zones.TryGetValue(nextZoneName, out Zone nextZone))
                    currentZone = nextZone;
                else
                    return null;
            }

            return currentZone;
        }

        private static Zone[] DeleteZone(Zone rootZone, string domain)
        {
            Zone currentZone = GetZone(rootZone, domain);
            if (currentZone == null)
                return null;

            if (!currentZone._authoritativeZone && (currentZone._zoneName.Equals("root-servers.net", StringComparison.CurrentCultureIgnoreCase)))
                return null; //cannot delete root-servers.net

            currentZone._entries.Clear();

            List<Zone> deletedSubDomains = new List<Zone>();

            DeleteSubDomains(currentZone, deletedSubDomains);

            DeleteEmptyParentZones(currentZone);

            return deletedSubDomains.ToArray();
        }

        private static bool DeleteSubDomains(Zone currentZone, List<Zone> deletedSubDomains)
        {
            if (currentZone._authoritativeZone)
            {
                if (currentZone._entries.ContainsKey(DnsResourceRecordType.SOA))
                    return false; //this is a zone so return false
            }
            else
            {
                //cache zone
                if (currentZone._zoneName.Equals("root-servers.net", StringComparison.CurrentCultureIgnoreCase))
                    return false; //cannot delete root-servers.net
            }

            currentZone._entries.Clear();
            deletedSubDomains.Add(currentZone);

            List<Zone> subDomainsToDelete = new List<Zone>();

            foreach (KeyValuePair<string, Zone> zone in currentZone._zones)
            {
                if (DeleteSubDomains(zone.Value, deletedSubDomains))
                    subDomainsToDelete.Add(zone.Value);
            }

            foreach (Zone subDomain in subDomainsToDelete)
                currentZone._zones.TryRemove(subDomain._zoneLabel, out Zone deletedValue);

            return (currentZone._zones.Count == 0);
        }

        private static void DeleteEmptyParentZones(Zone currentZone)
        {
            while (true)
            {
                if ((currentZone._entries.Count > 0) || (currentZone._zones.Count > 0))
                    break;

                currentZone._parentZone._zones.TryRemove(currentZone._zoneLabel, out Zone deletedZone);

                currentZone = currentZone._parentZone;
            }
        }

        private DnsResourceRecord[] QueryRecords(DnsResourceRecordType type, bool bypassCNAME = false)
        {
            if (!bypassCNAME && _entries.TryGetValue(DnsResourceRecordType.CNAME, out DnsResourceRecord[] existingCNAMERecords))
            {
                if (_authoritativeZone)
                    return existingCNAMERecords;

                return FilterExpiredRecords(existingCNAMERecords);
            }

            if (_entries.TryGetValue(type, out DnsResourceRecord[] existingRecords))
            {
                if (_authoritativeZone)
                    return existingRecords;

                return FilterExpiredRecords(existingRecords);
            }

            return null;
        }

        private DnsResourceRecord[] GetAllRecords(bool includeSubDomains)
        {
            List<DnsResourceRecord> allRecords = new List<DnsResourceRecord>();

            foreach (KeyValuePair<DnsResourceRecordType, DnsResourceRecord[]> entry in _entries)
            {
                if (entry.Key != DnsResourceRecordType.ANY)
                    allRecords.AddRange(entry.Value);
            }

            if (includeSubDomains)
            {
                foreach (KeyValuePair<string, Zone> zone in _zones)
                {
                    if (!zone.Value._entries.ContainsKey(DnsResourceRecordType.SOA))
                        allRecords.AddRange(zone.Value.GetAllRecords(true));
                }
            }

            return allRecords.ToArray();
        }

        private void SetRecords(DnsResourceRecordType type, DnsResourceRecord[] records)
        {
            if (type == DnsResourceRecordType.CNAME)
            {
                //delete all sub zones and entries except SOA
                _zones.Clear();

                foreach (DnsResourceRecordType key in _entries.Keys)
                {
                    if (key != DnsResourceRecordType.SOA)
                        _entries.TryRemove(key, out DnsResourceRecord[] removedValues);
                }
            }

            _entries.AddOrUpdate(type, records, delegate (DnsResourceRecordType key, DnsResourceRecord[] existingRecords)
            {
                return records;
            });
        }

        private void AddRecord(DnsResourceRecord record)
        {
            _entries.AddOrUpdate(record.Type, new DnsResourceRecord[] { record }, delegate (DnsResourceRecordType key, DnsResourceRecord[] existingRecords)
            {
                foreach (DnsResourceRecord existingRecord in existingRecords)
                {
                    if (record.RDATA.Equals(existingRecord.RDATA))
                        throw new DnsServerException("Resource record already exists.");
                }

                DnsResourceRecord[] newValue = new DnsResourceRecord[existingRecords.Length + 1];
                existingRecords.CopyTo(newValue, 0);

                newValue[newValue.Length - 1] = record;

                return newValue;
            });
        }

        private void DeleteRecord(DnsResourceRecord record)
        {
            if (_entries.TryGetValue(record.Type, out DnsResourceRecord[] existingRecords))
            {
                bool recordFound = false;

                for (int i = 0; i < existingRecords.Length; i++)
                {
                    if (record.RDATA.Equals(existingRecords[i].RDATA))
                    {
                        existingRecords[i] = null;
                        recordFound = true;
                        break;
                    }
                }

                if (!recordFound)
                    throw new DnsServerException("Resource record does not exists.");

                if (existingRecords.Length == 1)
                {
                    DeleteRecords(record.Type);
                }
                else
                {
                    DnsResourceRecord[] newRecords = new DnsResourceRecord[existingRecords.Length - 1];

                    for (int i = 0, j = 0; i < existingRecords.Length; i++)
                    {
                        if (existingRecords[i] != null)
                            newRecords[j++] = existingRecords[i];
                    }

                    _entries.AddOrUpdate(record.Type, newRecords, delegate (DnsResourceRecordType key, DnsResourceRecord[] oldValue)
                    {
                        return newRecords;
                    });
                }
            }
        }

        private void DeleteRecords(DnsResourceRecordType type)
        {
            _entries.TryRemove(type, out DnsResourceRecord[] existingValues);

            DeleteEmptyParentZones(this);
        }

        private DnsResourceRecord[] FilterExpiredRecords(DnsResourceRecord[] records)
        {
            if (records.Length == 1)
            {
                if (records[0].TTLValue < 1)
                    return null;

                return records;
            }

            List<DnsResourceRecord> newRecords = new List<DnsResourceRecord>(records.Length);

            foreach (DnsResourceRecord record in records)
            {
                if (record.TTLValue > 0)
                    newRecords.Add(record);
            }

            if (newRecords.Count > 0)
                return newRecords.ToArray();

            return null;
        }

        private DnsResourceRecord[] GetClosestNameServers()
        {
            Zone currentZone = this;
            DnsResourceRecord[] nsRecords = null;

            while (currentZone != null)
            {
                nsRecords = currentZone.QueryRecords(DnsResourceRecordType.NS);
                if ((nsRecords != null) && (nsRecords.Length > 0) && (nsRecords[0].Type == DnsResourceRecordType.NS))
                    return nsRecords;

                currentZone = currentZone._parentZone;
            }

            return null;
        }

        private DnsResourceRecord[] GetClosestAuthority()
        {
            Zone currentZone = this;
            DnsResourceRecord[] nsRecords = null;

            while (currentZone != null)
            {
                nsRecords = currentZone.QueryRecords(DnsResourceRecordType.SOA);
                if ((nsRecords != null) && (nsRecords.Length > 0) && (nsRecords[0].Type == DnsResourceRecordType.SOA))
                    return nsRecords;

                currentZone = currentZone._parentZone;
            }

            return null;
        }

        private static DnsResourceRecord[] GetGlueRecords(Zone rootZone, string domain, DnsResourceRecordType type)
        {
            Zone currentZone = GetZone(rootZone, domain);
            if (currentZone != null)
            {
                DnsResourceRecord[] records = currentZone.QueryRecords(type);
                if ((records != null) && (records.Length > 0) && (records[0].Type == type))
                    return records;
            }

            return null;
        }

        private void GetAuthoritativeZones(List<Zone> zones)
        {
            DnsResourceRecord[] soa = QueryRecords(DnsResourceRecordType.SOA, true);
            if ((soa != null) && (soa[0].Type == DnsResourceRecordType.SOA))
                zones.Add(this);

            foreach (KeyValuePair<string, Zone> entry in _zones)
                entry.Value.GetAuthoritativeZones(zones);
        }

        private static DnsDatagram QueryAuthoritative(Zone rootZone, DnsDatagram request)
        {
            DnsQuestionRecord question = request.Question[0];
            string domain = question.Name.ToLower();

            Zone closestZone = FindClosestZone(rootZone, domain);

            if (closestZone._disabled)
                return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.Refused, 1, 0, 0, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });

            if (closestZone._zoneName.Equals(domain))
            {
                //zone found
                DnsResourceRecord[] records = closestZone.QueryRecords(question.Type);
                if (records == null)
                {
                    //record type not found
                    DnsResourceRecord[] closestAuthority = closestZone.GetClosestAuthority();

                    if (closestAuthority == null)
                        return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.Refused, 1, 0, 0, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });

                    return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, true, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.NoError, 1, 0, 1, 0), request.Question, new DnsResourceRecord[] { }, closestAuthority, new DnsResourceRecord[] { });
                }
                else
                {
                    //record type found
                    return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, true, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.NoError, 1, (ushort)records.Length, 0, 0), request.Question, records, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });
                }
            }
            else
            {
                //zone doesnt exists
                DnsResourceRecord[] closestAuthority = closestZone.GetClosestAuthority();

                if (closestAuthority == null)
                    return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.Refused, 1, 0, 0, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });

                return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, true, false, request.Header.RecursionDesired, false, false, false, DnsResponseCode.NameError, 1, 0, 1, 0), request.Question, new DnsResourceRecord[] { }, closestAuthority, new DnsResourceRecord[] { });
            }
        }

        private static DnsDatagram QueryCache(Zone rootZone, DnsDatagram request)
        {
            DnsQuestionRecord question = request.Question[0];
            string domain = question.Name.ToLower();

            Zone closestZone = FindClosestZone(rootZone, domain);

            if (closestZone._zoneName.Equals(domain))
            {
                DnsResourceRecord[] records = closestZone.QueryRecords(question.Type);
                if (records != null)
                {
                    if (records[0].RDATA is DnsEmptyRecord)
                        return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.NoError, 1, 0, 1, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { (records[0].RDATA as DnsEmptyRecord).Authority }, new DnsResourceRecord[] { });

                    if (records[0].RDATA is DnsNXRecord)
                        return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.NameError, 1, 0, 1, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { (records[0].RDATA as DnsNXRecord).Authority }, new DnsResourceRecord[] { });

                    if (records[0].RDATA is DnsANYRecord)
                    {
                        DnsANYRecord anyRR = records[0].RDATA as DnsANYRecord;
                        return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.NoError, 1, Convert.ToUInt16(anyRR.Records.Length), 0, 0), request.Question, anyRR.Records, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });
                    }

                    return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.NoError, 1, (ushort)records.Length, 0, 0), request.Question, records, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });
                }
            }

            DnsResourceRecord[] nameServers = closestZone.GetClosestNameServers();
            if (nameServers != null)
            {
                List<DnsResourceRecord> glueRecords = new List<DnsResourceRecord>();

                foreach (DnsResourceRecord nameServer in nameServers)
                {
                    string nsDomain = (nameServer.RDATA as DnsNSRecord).NSDomainName;

                    DnsResourceRecord[] glueAs = GetGlueRecords(rootZone, nsDomain, DnsResourceRecordType.A);
                    if (glueAs != null)
                        glueRecords.AddRange(glueAs);

                    DnsResourceRecord[] glueAAAAs = GetGlueRecords(rootZone, nsDomain, DnsResourceRecordType.AAAA);
                    if (glueAAAAs != null)
                        glueRecords.AddRange(glueAAAAs);
                }

                DnsResourceRecord[] additional = glueRecords.ToArray();

                return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.NoError, 1, 0, (ushort)nameServers.Length, (ushort)additional.Length), request.Question, new DnsResourceRecord[] { }, nameServers, additional);
            }

            return new DnsDatagram(new DnsHeader(request.Header.Identifier, true, DnsOpcode.StandardQuery, false, false, request.Header.RecursionDesired, true, false, false, DnsResponseCode.Refused, 1, 0, 0, 0), request.Question, new DnsResourceRecord[] { }, new DnsResourceRecord[] { }, new DnsResourceRecord[] { });
        }

        #endregion

        #region internal

        internal static Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> GroupRecords(ICollection<DnsResourceRecord> records)
        {
            Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = new Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>>();

            foreach (DnsResourceRecord record in records)
            {
                Dictionary<DnsResourceRecordType, List<DnsResourceRecord>> groupedByTypeRecords;

                if (groupedByDomainRecords.ContainsKey(record.Name))
                {
                    groupedByTypeRecords = groupedByDomainRecords[record.Name];
                }
                else
                {
                    groupedByTypeRecords = new Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>();
                    groupedByDomainRecords.Add(record.Name, groupedByTypeRecords);
                }

                List<DnsResourceRecord> groupedRecords;

                if (groupedByTypeRecords.ContainsKey(record.Type))
                {
                    groupedRecords = groupedByTypeRecords[record.Type];
                }
                else
                {
                    groupedRecords = new List<DnsResourceRecord>();
                    groupedByTypeRecords.Add(record.Type, groupedRecords);
                }

                groupedRecords.Add(record);
            }

            return groupedByDomainRecords;
        }

        internal DnsDatagram Query(DnsDatagram request)
        {
            if (_authoritativeZone)
                return QueryAuthoritative(this, request);

            return QueryCache(this, request);
        }

        internal void CacheResponse(DnsDatagram response)
        {
            if (!response.Header.IsResponse)
                return;

            //combine all records in the response
            List<DnsResourceRecord> allRecords = new List<DnsResourceRecord>();

            switch (response.Header.RCODE)
            {
                case DnsResponseCode.NameError:
                    if (response.Authority.Length > 0)
                    {
                        DnsResourceRecord authority = response.Authority[0];
                        if (authority.Type == DnsResourceRecordType.SOA)
                        {
                            foreach (DnsQuestionRecord question in response.Question)
                            {
                                DnsResourceRecord record = new DnsResourceRecord(question.Name, question.Type, DnsClass.IN, DEFAULT_RECORD_TTL, new DnsNXRecord(authority));
                                record.SetExpiry();

                                CreateZone(this, question.Name).SetRecords(question.Type, new DnsResourceRecord[] { record });
                            }
                        }
                    }
                    break;

                case DnsResponseCode.NoError:
                    if ((response.Answer.Length == 0) && (response.Authority.Length > 0))
                    {
                        DnsResourceRecord authority = response.Authority[0];
                        if (authority.Type == DnsResourceRecordType.SOA)
                        {
                            foreach (DnsQuestionRecord question in response.Question)
                            {
                                DnsResourceRecord record = new DnsResourceRecord(question.Name, question.Type, DnsClass.IN, DEFAULT_RECORD_TTL, new DnsEmptyRecord(authority));
                                record.SetExpiry();

                                CreateZone(this, question.Name).SetRecords(question.Type, new DnsResourceRecord[] { record });
                            }
                        }
                    }
                    else
                    {
                        allRecords.AddRange(response.Answer);
                    }

                    break;

                default:
                    return; //nothing to do
            }

            allRecords.AddRange(response.Authority);
            allRecords.AddRange(response.Additional);

            //set expiry for cached records
            foreach (DnsResourceRecord record in allRecords)
                record.SetExpiry();

            SetRecords(allRecords);

            //cache for ANY request
            if ((response.Question[0].Type == DnsResourceRecordType.ANY) && (response.Answer.Length > 0))
            {
                DnsResourceRecord anyRR = new DnsResourceRecord(response.Question[0].Name, DnsResourceRecordType.ANY, DnsClass.IN, DEFAULT_RECORD_TTL, new DnsANYRecord(response.Answer));
                anyRR.SetExpiry();

                CreateZone(this, response.Question[0].Name).SetRecords(DnsResourceRecordType.ANY, new DnsResourceRecord[] { anyRR });
            }
        }

        #endregion

        #region public

        public void SetRecords(string domain, DnsResourceRecordType type, uint ttl, DnsResourceRecordData[] records)
        {
            DnsResourceRecord[] resourceRecords = new DnsResourceRecord[records.Length];

            for (int i = 0; i < records.Length; i++)
                resourceRecords[i] = new DnsResourceRecord(domain, type, DnsClass.IN, ttl, records[i]);

            CreateZone(this, domain).SetRecords(type, resourceRecords);
        }

        public void SetRecords(ICollection<DnsResourceRecord> records)
        {
            Dictionary<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByDomainRecords = GroupRecords(records);

            //add grouped records
            foreach (KeyValuePair<string, Dictionary<DnsResourceRecordType, List<DnsResourceRecord>>> groupedByTypeRecords in groupedByDomainRecords)
            {
                string domain = groupedByTypeRecords.Key;
                Zone zone = CreateZone(this, domain);

                foreach (KeyValuePair<DnsResourceRecordType, List<DnsResourceRecord>> groupedRecords in groupedByTypeRecords.Value)
                {
                    DnsResourceRecordType type = groupedRecords.Key;
                    DnsResourceRecord[] resourceRecords = groupedRecords.Value.ToArray();

                    zone.SetRecords(type, resourceRecords);
                }
            }
        }

        public void AddRecord(string domain, DnsResourceRecordType type, uint ttl, DnsResourceRecordData record)
        {
            DnsResourceRecord rr = new DnsResourceRecord(domain, type, DnsClass.IN, ttl, record);
            CreateZone(this, domain).AddRecord(rr);
        }

        public void UpdateRecord(DnsResourceRecord oldRecord, DnsResourceRecord newRecord)
        {
            if (oldRecord.Type != newRecord.Type)
                throw new DnsServerException("Cannot update record: new record must be of same type.");

            if (oldRecord.Type == DnsResourceRecordType.SOA)
                throw new DnsServerException("Cannot update record: use SetRecords() for updating SOA record.");

            Zone currentZone = GetZone(this, oldRecord.Name);
            if (currentZone == null)
                throw new DnsServerException("Cannot update record: old record does not exists.");

            switch (oldRecord.Type)
            {
                case DnsResourceRecordType.CNAME:
                case DnsResourceRecordType.PTR:
                    if (oldRecord.Name.Equals(newRecord.Name, StringComparison.CurrentCultureIgnoreCase))
                    {
                        currentZone.SetRecords(newRecord.Type, new DnsResourceRecord[] { newRecord });
                    }
                    else
                    {
                        currentZone.DeleteRecords(oldRecord.Type);
                        CreateZone(this, newRecord.Name).SetRecords(newRecord.Type, new DnsResourceRecord[] { newRecord });
                    }
                    break;

                default:
                    currentZone.DeleteRecord(oldRecord);

                    if (oldRecord.Name.Equals(newRecord.Name, StringComparison.CurrentCultureIgnoreCase))
                        currentZone.AddRecord(newRecord);
                    else
                        CreateZone(this, newRecord.Name).AddRecord(newRecord);

                    break;
            }
        }

        public void DeleteRecord(string domain, DnsResourceRecordType type, DnsResourceRecordData record)
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone != null)
                currentZone.DeleteRecord(new DnsResourceRecord(domain, type, DnsClass.IN, 0, record));
        }

        public void DeleteRecords(string domain, DnsResourceRecordType type)
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone != null)
                currentZone.DeleteRecords(type);
        }

        public DnsResourceRecord[] GetAllRecords(string domain = "", bool includeSubDomains = true)
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone == null)
                return null;

            DnsResourceRecord[] records = currentZone.GetAllRecords(includeSubDomains);
            if (records != null)
                return records;

            return new DnsResourceRecord[] { };
        }

        public string[] ListSubZones(string domain = "")
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone == null)
                return new string[] { }; //no zone for given domain

            string[] subZoneNames = new string[currentZone._zones.Keys.Count];
            currentZone._zones.Keys.CopyTo(subZoneNames, 0);

            return subZoneNames;
        }

        public ZoneInfo[] ListAuthoritativeZones(string domain = "")
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone == null)
                return new ZoneInfo[] { }; //no zone for given domain

            List<Zone> zones = new List<Zone>();
            currentZone.GetAuthoritativeZones(zones);

            List<ZoneInfo> zoneNames = new List<ZoneInfo>();

            foreach (Zone zone in zones)
                zoneNames.Add(new ZoneInfo(zone));

            return zoneNames.ToArray();
        }

        public string[] DeleteZone(string domain)
        {
            Zone[] deletedZones = DeleteZone(this, domain);
            if (deletedZones == null)
                return new string[] { };

            List<string> deletedZoneNames = new List<string>();

            foreach (Zone deletedZone in deletedZones)
                deletedZoneNames.Add(deletedZone._zoneName);

            return deletedZoneNames.ToArray();
        }

        public void DisableZone(string domain)
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone != null)
                currentZone._disabled = true;
        }

        public void EnableZone(string domain)
        {
            Zone currentZone = GetZone(this, domain);
            if (currentZone != null)
                currentZone._disabled = false;
        }

        public void Flush()
        {
            _zones.Clear();
            _entries.Clear();
        }

        #endregion

        public class ZoneInfo
        {
            #region variables

            readonly string _zoneName;
            readonly bool _disabled;

            #endregion

            #region constructor

            public ZoneInfo(string zoneName, bool disabled)
            {
                _zoneName = zoneName;
                _disabled = disabled;
            }

            public ZoneInfo(Zone zone)
            {
                _zoneName = zone._zoneName;
                _disabled = zone._disabled;
            }

            #endregion

            #region properties

            public string ZoneName
            { get { return _zoneName; } }

            public bool Disabled
            { get { return _disabled; } }

            #endregion
        }

        class DnsNXRecord : DnsResourceRecordData
        {
            #region variables

            DnsResourceRecord _authority;

            #endregion

            #region constructor

            public DnsNXRecord(DnsResourceRecord authority)
            {
                _authority = authority;
            }

            #endregion

            #region protected

            protected override void Parse(Stream s)
            { }

            protected override void WriteRecordData(Stream s, List<DnsDomainOffset> domainEntries)
            { }

            #endregion

            #region public

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                    return false;

                if (ReferenceEquals(this, obj))
                    return true;

                DnsNXRecord other = obj as DnsNXRecord;
                if (other == null)
                    return false;

                return _authority.Equals(other._authority);
            }

            public override int GetHashCode()
            {
                return _authority.GetHashCode();
            }

            #endregion

            #region properties

            public DnsResourceRecord Authority
            { get { return _authority; } }

            #endregion
        }

        class DnsEmptyRecord : DnsResourceRecordData
        {
            #region variables

            DnsResourceRecord _authority;

            #endregion

            #region constructor

            public DnsEmptyRecord(DnsResourceRecord authority)
            {
                _authority = authority;
            }

            #endregion

            #region protected

            protected override void Parse(Stream s)
            { }

            protected override void WriteRecordData(Stream s, List<DnsDomainOffset> domainEntries)
            { }

            #endregion

            #region public

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                    return false;

                if (ReferenceEquals(this, obj))
                    return true;

                DnsEmptyRecord other = obj as DnsEmptyRecord;
                if (other == null)
                    return false;

                return _authority.Equals(other._authority);
            }

            public override int GetHashCode()
            {
                return _authority.GetHashCode();
            }

            #endregion

            #region properties

            public DnsResourceRecord Authority
            { get { return _authority; } }

            #endregion
        }

        class DnsANYRecord : DnsResourceRecordData
        {
            #region variables

            DnsResourceRecord[] _records;

            #endregion

            #region constructor

            public DnsANYRecord(DnsResourceRecord[] records)
            {
                _records = records;
            }

            public DnsANYRecord(Stream s)
                : base(s)
            { }

            #endregion

            #region protected

            protected override void Parse(Stream s)
            { }

            protected override void WriteRecordData(Stream s, List<DnsDomainOffset> domainEntries)
            { }

            #endregion

            #region public

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj))
                    return false;

                if (ReferenceEquals(this, obj))
                    return true;

                DnsANYRecord other = obj as DnsANYRecord;
                if (other == null)
                    return false;

                return true;
            }

            public override int GetHashCode()
            {
                return 0;
            }

            #endregion

            #region properties

            public DnsResourceRecord[] Records
            { get { return _records; } }

            #endregion
        }
    }
}
