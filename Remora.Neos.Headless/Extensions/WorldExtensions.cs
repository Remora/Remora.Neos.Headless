//
//  SPDX-FileName: WorldExtensions.cs
//  SPDX-FileCopyrightText: Copyright (c) Jarl Gullberg
//  SPDX-License-Identifier: AGPL-3.0-or-later
//

using CloudX.Shared;
using FrooxEngine;
using Microsoft.Extensions.Logging;
using WorldStartupParameters = Remora.Neos.Headless.Configuration.WorldStartupParameters;

namespace Remora.Neos.Headless.Extensions;

/// <summary>
/// Defines extensions for the <see cref="World"/> class.
/// </summary>
public static class WorldExtensions
{
    /// <summary>
    /// Sets various startup parameters for the given world, potentially updating the startup parameter set.
    /// </summary>
    /// <param name="world">The world.</param>
    /// <param name="startupParameters">The startup parameters.</param>
    /// <param name="log">The logging instance to use.</param>
    /// <returns>The updated startup parameters.</returns>
    public static async Task<WorldStartupParameters> SetParametersAsync
    (
        this World world,
        WorldStartupParameters startupParameters,
        ILogger log
    )
    {
        if (startupParameters.SessionName is not null)
        {
            world.Name = startupParameters.SessionName;
        }

        if (startupParameters.Tags is not null)
        {
            world.Tags = startupParameters.Tags;
        }

        world.AccessLevel = startupParameters.AccessLevel;
        world.HideFromListing = startupParameters.HideFromPublicListing is true;
        world.MaxUsers = startupParameters.MaxUsers;
        world.MobileFriendly = startupParameters.MobileFriendly;
        world.Description = startupParameters.Description;
        world.ForceFullUpdateCycle = !startupParameters.AutoSleep;
        world.SaveOnExit = startupParameters.SaveOnExit;

        var correspondingWorldId = startupParameters.OverrideCorrespondingWorldID;
        if (correspondingWorldId is not null && correspondingWorldId.IsValid)
        {
            world.CorrespondingWorldId = correspondingWorldId.ToString();
        }

        if (startupParameters.AwayKickMinutes > 0.0)
        {
            world.AwayKickEnabled = true;
            world.AwayKickMinutes = (float)startupParameters.AwayKickMinutes;
        }

        world.SetCloudVariableParameters(startupParameters, log);
        world.ConfigureParentSessions(startupParameters, log);

        await world.ConfigurePermissionsAsync(startupParameters, log);
        await world.SendAutomaticInvitesAsync(startupParameters, log);

        startupParameters = await world.ConfigureSaveAsAsync(startupParameters, log);

        return startupParameters;
    }

    private static void ConfigureParentSessions(this World world, WorldStartupParameters startupParameters, ILogger log)
    {
        if (startupParameters.ParentSessionIDs is null)
        {
            return;
        }

        var sessionIDs = new List<string>();
        foreach (var parentSessionID in startupParameters.ParentSessionIDs)
        {
            if (!SessionInfo.IsValidSessionId(parentSessionID))
            {
                log.LogWarning("Parent session ID {ID} is invalid", parentSessionID);
                continue;
            }

            log.LogInformation("Parent session ID: {ID}", parentSessionID);
            sessionIDs.Add(parentSessionID);
        }

        world.ParentSessionIds = sessionIDs;
    }

    private static async Task SendAutomaticInvitesAsync
    (
        this World world,
        WorldStartupParameters startupParameters,
        ILogger log
    )
    {
        if (startupParameters.AutoInviteUsernames is null)
        {
            return;
        }

        foreach (var username in startupParameters.AutoInviteUsernames)
        {
            var friend = world.Engine.Cloud.Friends.FindFriend
            (
                f => f.FriendUsername.Equals(username, StringComparison.InvariantCultureIgnoreCase)
            );

            if (friend is null)
            {
                log.LogWarning("{Username} is not in the friends list, cannot auto-invite", username);
                continue;
            }

            var messages = world.Engine.Cloud.Messages.GetUserMessages(friend.FriendUserId);
            if (startupParameters.AutoInviteMessage is not null)
            {
                if (!await messages.SendTextMessage(startupParameters.AutoInviteMessage))
                {
                    log.LogWarning("Failed to send custom auto-invite message");
                }
            }

            world.AllowUserToJoin(friend.FriendUserId);
            if (!await messages.SendMessage(messages.CreateInviteMessage(world)))
            {
                log.LogWarning("Failed to send auto-invite");
            }
            else
            {
                log.LogInformation("{Username} invited", username);
            }
        }
    }

    private static Task ConfigurePermissionsAsync
    (
        this World world,
        WorldStartupParameters startupParameters,
        ILogger log
    )
    {
        return world.Coroutines.StartTask
        (
            static async args =>
            {
                await default(NextUpdate);
                if (!args.Startup.KeepOriginalRoles)
                {
                    args.World.Permissions.DefaultUserPermissions.Clear();
                }

                if (args.Startup.DefaultUserRoles is null)
                {
                    return;
                }

                foreach (var (user, role) in args.Startup.DefaultUserRoles)
                {
                    var userByName = await args.World.Engine.Cloud.GetUserByName(user);
                    if (userByName.IsError)
                    {
                        args.Log.LogWarning("User {User} not found: {Reason}", user, userByName.State);
                        continue;
                    }

                    var roleByName = args.World.Permissions.FindRoleByName(role);
                    if (roleByName is null)
                    {
                        args.Log.LogWarning("Role {Role} not available for world {World}", role, args.World.RawName);
                        continue;
                    }

                    var permissionSet = args.World.Permissions.FilterRole(roleByName);
                    if (permissionSet != roleByName)
                    {
                        args.Log.LogWarning
                        (
                            "Cannot use default role {DefaultRole} for {Role} because it's higher than the host role {HostRole} in world {World}",
                            roleByName.RoleName.Value,
                            role,
                            permissionSet.RoleName.Value,
                            args.World.RawName
                        );
                    }

                    args.World.Permissions.DefaultUserPermissions.Remove(userByName.Entity.Id);
                    args.World.Permissions.DefaultUserPermissions.Add(userByName.Entity.Id, permissionSet);
                }
            },
            (World: world, Startup: startupParameters, Log: log)
        );
    }

    private static void SetCloudVariableParameters
    (
        this World world,
        WorldStartupParameters startupParameters,
        ILogger log
    )
    {
        if (startupParameters.RoleCloudVariable is not null)
        {
            if (!CloudVariableHelper.IsValidPath(startupParameters.RoleCloudVariable))
            {
                log.LogWarning("Invalid RoleCloudVariable: {Variable}", startupParameters.RoleCloudVariable);
            }
            else
            {
                world.Permissions.DefaultRoleCloudVariable = startupParameters.RoleCloudVariable;
            }
        }

        if (startupParameters.AllowUserCloudVariable is not null)
        {
            if (!CloudVariableHelper.IsValidPath(startupParameters.AllowUserCloudVariable))
            {
                log.LogWarning("Invalid AllowUserCloudVariable: {Variable}", startupParameters.AllowUserCloudVariable);
            }
            else
            {
                world.AllowUserCloudVariable = startupParameters.AllowUserCloudVariable;
            }
        }

        if (startupParameters.DenyUserCloudVariable is not null)
        {
            if (!CloudVariableHelper.IsValidPath(startupParameters.DenyUserCloudVariable))
            {
                log.LogWarning("Invalid DenyUserCloudVariable: {Variable}", startupParameters.DenyUserCloudVariable);
            }
            else
            {
                world.DenyUserCloudVariable = startupParameters.DenyUserCloudVariable;
            }
        }

        if (startupParameters.RequiredUserJoinCloudVariable is not null)
        {
            if (!CloudVariableHelper.IsValidPath(startupParameters.RequiredUserJoinCloudVariable))
            {
                log.LogWarning
                    ("Invalid RequiredUserJoinCloudVariable: {Variable}", startupParameters.RequiredUserJoinCloudVariable);
            }
            else
            {
                world.RequiredUserJoinCloudVariable = startupParameters.RequiredUserJoinCloudVariable;
            }
        }

        if (startupParameters.RequiredUserJoinCloudVariableDenyMessage is not null)
        {
            world.RequiredUserJoinCloudVariableDenyMessage = startupParameters.RequiredUserJoinCloudVariableDenyMessage;
        }
    }

    private static async Task<WorldStartupParameters> ConfigureSaveAsAsync
    (
        this World world,
        WorldStartupParameters startupParameters,
        ILogger log
    )
    {
        string? ownerID = null;
        switch (startupParameters.SaveAsOwner)
        {
            case SaveAsOwner.LocalMachine:
            {
                ownerID = $"M-{world.Engine.LocalDB.MachineID}";
                break;
            }
            case SaveAsOwner.CloudUser:
            {
                if (world.Engine.Cloud.CurrentUser is null)
                {
                    log.LogWarning("World is set to be saved under cloud user, but not user is logged in");
                    break;
                }

                ownerID = world.Engine.Cloud.CurrentUser.Id;
                break;
            }
            case null:
            {
                break;
            }
            default:
            {
                throw new ArgumentOutOfRangeException();
            }
        }

        if (ownerID is not null)
        {
            var record = world.CorrespondingRecord;
            if (record is null)
            {
                record = world.CreateNewRecord(ownerID);
                world.CorrespondingRecord = record;
            }
            else
            {
                record.OwnerId = ownerID;
                record.RecordId = RecordUtil.GenerateRecordID();
            }

            var transferer = new RecordOwnerTransferer(world.Engine, record.OwnerId, record.RecordId);
            log.LogInformation("Saving world under {SaveAs}", startupParameters.SaveAsOwner);

            var savedRecord = await Userspace.SaveWorld(world, record, transferer);
            log.LogInformation("Saved successfully");

            startupParameters = startupParameters with
            {
                SaveAsOwner = null,
                LoadWorldURL = savedRecord.URL
            };
        }
        return startupParameters;
    }
}