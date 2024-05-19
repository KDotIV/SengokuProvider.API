﻿using Npgsql;
using SengokuProvider.Library.Models.Events;
using SengokuProvider.Library.Models.Regions;
using SengokuProvider.Library.Services.Common;

namespace SengokuProvider.Library.Services.Events
{
    public class EventQueryService : IEventQueryService
    {
        private readonly string _connectionString;
        private readonly IntakeValidator _validator;
        public EventQueryService(string connectionString, IntakeValidator validator)
        {
            _connectionString = connectionString;
            _validator = validator;
        }
        public async Task<List<int>> QueryRelatedRegionsById(int regionId)
        {
            try
            {
                var relatedRegions = new List<int>();

                using (var conn = new NpgsqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = @"SELECT r2.id
                            FROM public.regions r1
                            JOIN regions r2 ON r1.province = r2.province
                            WHERE r1.id = @InputRegionId
                              AND r2.id != @InputRegionId;";

                        cmd.Parameters.AddWithValue("@InputRegionId", regionId);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                relatedRegions.Add(reader.GetInt32(0));
                            }
                        }
                    }
                }
                relatedRegions.Add(regionId);
                return relatedRegions;
            }
            catch (NpgsqlException ex)
            {
                throw new ApplicationException("Database error occurred: ", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException("Unexpected Error Occurred: ", ex);
            }
        }
        public async Task<List<RegionData>> GetRegionData(List<int> regionIds)
        {
            var regions = new List<RegionData>();
            try
            {
                using (var conn = new NpgsqlConnection(_connectionString))
                {
                    await conn.OpenAsync();
                    using (var cmd = new NpgsqlCommand("SELECT id, name, latitude, longitude, province FROM regions WHERE id = ANY(@Input)", conn))
                    {
                        // Passing the list as an array parameter
                        var param = new NpgsqlParameter("@Input", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer)
                        {
                            Value = regionIds.ToArray()
                        };
                        cmd.Parameters.Add(param);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                regions.Add(new RegionData
                                {
                                    Id = reader.GetInt32(reader.GetOrdinal("id")),
                                    Name = reader.GetString(reader.GetOrdinal("name")),
                                    Latitude = reader.GetDouble(reader.GetOrdinal("latitude")),
                                    Longitude = reader.GetDouble(reader.GetOrdinal("longitude")),
                                    Province = reader.GetString(reader.GetOrdinal("province"))
                                });
                            }
                        }
                    }

                    return regions;
                }
            }
            catch (NpgsqlException ex)
            {
                throw new ApplicationException("Database error occurred: ", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException("Unexpected Error Occurred: ", ex);
            }
        }
        public async Task<List<AddressEventResult>> QueryEventsByLocation(GetTournamentsByLocationCommand command, int pageNumber)
        {
            if (command == null || command.RegionId == 0) throw new ArgumentNullException(nameof(command));
            try
            {
                var currentRegions = await QueryRelatedRegionsById(command.RegionId);

                var sortedAddresses = new List<AddressEventResult>();

                using (var conn = new NpgsqlConnection(_connectionString))
                {
                    await conn.OpenAsync();

                    var regionData = await GetRegionData(currentRegions);
                    var regionIds = new List<int>();
                    var locationReference = regionData.FirstOrDefault(x => x.Id == command.RegionId);

                    foreach (var region in regionData)
                    {
                        regionIds.Add(region.Id);
                    }

                    using (var cmd = new NpgsqlCommand())
                    {
                        cmd.Connection = conn;
                        cmd.CommandText = @"
                            SELECT 
                                a.address, a.latitude, a.longitude, 
                                e.event_name, e.event_description, e.region, e.start_time, e.end_time, e.link_id,
                                SQRT(
                                    POW(a.longitude - @ReferenceLongitude, 2) + POW(a.latitude - @ReferenceLatitude, 2)
                                ) AS distance
                            FROM 
                                events e
                            JOIN 
                                addresses a ON e.address_id = a.id
                            WHERE
                                e.region = ANY(@RegionIds)
                                AND e.start_time >= CURRENT_DATE
                            ORDER BY
                                e.start_time ASC,
                                distance ASC
                            LIMIT @PerPage;";

                        var regionIdsParam = cmd.CreateParameter();
                        regionIdsParam.ParameterName = "RegionIds";
                        regionIdsParam.Value = regionIds.ToArray();
                        regionIdsParam.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Integer;

                        cmd.Parameters.Add(regionIdsParam);
                        cmd.Parameters.AddWithValue("@ReferenceLatitude", locationReference.Latitude);
                        cmd.Parameters.AddWithValue("@ReferenceLongitude", locationReference.Longitude);
                        cmd.Parameters.AddWithValue("@PerPage", command.PerPage);

                        using (var reader = await cmd.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                sortedAddresses.Add(new AddressEventResult
                                {
                                    Address = reader.GetString(reader.GetOrdinal("address")),
                                    Latitude = reader.GetDouble(reader.GetOrdinal("latitude")),
                                    Longitude = reader.GetDouble(reader.GetOrdinal("longitude")),
                                    Distance = Math.Round(reader.GetDouble(reader.GetOrdinal("distance")), 4),
                                    EventName = reader.GetString(reader.GetOrdinal("event_name")),
                                    EventDescription = reader.GetString(reader.GetOrdinal("event_description")),
                                    Region = reader.GetInt32(reader.GetOrdinal("region")),
                                    StartTime = reader.GetDateTime(reader.GetOrdinal("start_time")),
                                    EndTime = reader.GetDateTime(reader.GetOrdinal("end_time")),
                                    LinkId = reader.GetInt32(reader.GetOrdinal("link_id"))
                                });
                            }
                        }
                    }
                }

                return sortedAddresses;
            }
            catch (NpgsqlException ex)
            {
                throw new ApplicationException($"Database error occurred: {ex.InnerException}", ex);
            }
            catch (Exception ex)
            {
                throw new ApplicationException($"Unexpected Error Occurred: {ex.StackTrace}", ex);
            }
        }
    }
}