﻿namespace Scrum.Tests

open System
open System.Threading
open System.Data.SQLite
open Scrum.Application.StoryAggregateRequest.GetStoryByIdQuery
open Swensen.Unquote
open Xunit
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Application.DomainEventRequest
open Scrum.Infrastructure

module A =
    let createStoryCommand () : CreateStoryCommand = { Id = Guid.NewGuid(); Title = "title"; Description = Some "description" }

    let updateStoryCommand (source: CreateStoryCommand) = { Id = source.Id; Title = source.Title; Description = source.Description }

    let addTaskToStoryCommand () : AddTaskToStoryCommand =
        { TaskId = Guid.NewGuid()
          StoryId = Guid.Empty
          Title = "title"
          Description = Some "description" }

    let updateTaskCommand (cmd: AddTaskToStoryCommand) =
        { StoryId = cmd.StoryId
          TaskId = cmd.TaskId
          Title = cmd.Title
          Description = cmd.Description }

module Database =
    // SQLite driver created the database at the path if the file doesn't
    // already exist. The default directory is
    // src/Scrum.Tests/bin/Debug/net7.0/scrum_test.sqlite whereas we want the
    // database at the root of the Git repository.
    let connectionString = "URI=file:../../../../../scrum_test.sqlite"

    let missingId () = Guid.NewGuid()

    // Call before a test run (from constructor), not after (Dispose). This way
    // data is left in the database for troubleshooting.
    let reset () : unit =
        // Organize in reverse dependency order.
        let sql =
            [| "delete from tasks"; "delete from stories"; "delete from domain_events" |]
        use connection = new SQLiteConnection(connectionString)
        connection.Open()
        use transaction = connection.BeginTransaction()
        sql
        |> Array.iter (fun sql ->
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.ExecuteNonQuery() |> ignore)
        transaction.Commit()

open Database

module Fake =
    let userIdentityService roles =
        { new IUserIdentity with
            member _.GetCurrent() = ScrumIdentity.Authenticated("1", roles) }

    let fixedClock =
        { new ISystemClock with
            member _.CurrentUtc() = DateTime(2023, 1, 1, 6, 0, 0) }

    let nullLogger =
        { new ILogger with
            member _.LogRequestPayload _ _ = ()
            member _.LogRequestDuration _ _ = ()
            member _.LogException _ = ()
            member _.LogError _ = ()
            member _.LogInformation _ = ()
            member _.LogDebug _ = () }

    let appEnvWithRoles (roles: ScrumRole list) =
        new AppEnv(connectionString, userIdentityService roles, systemClock = fixedClock, logger = nullLogger)   
    
    let defaultAppEnv () =
        appEnvWithRoles [ Member; Admin ]    

module Setup =
    let setupStoryAggregateRequests (env: IAppEnv) =
        let u = env.UserIdentity
        let r = env.StoryRepository
        let s = env.SystemClock
        let l = env.Logger
        let ct = CancellationToken.None

        // While these functions are async, we forgo the Async prefix to reduce
        // noise.
        {| CreateStory = CreateStoryCommand.runAsync u r s l ct
           AddTaskToStory = AddTaskToStoryCommand.runAsync u r s l ct
           GetStoryById = GetStoryByIdQuery.runAsync u r l ct
           DeleteStory = DeleteStoryCommand.runAsync u r l ct
           DeleteTask = DeleteTaskCommand.runAsync u r l ct
           UpdateStory = UpdateStoryCommand.runAsync u r s l ct
           UpdateTask = UpdateTaskCommand.runAsync u r s l ct
           Commit = fun _ -> env.CommitAsync ct |}

    let setupDomainEventRequests (env: IAppEnv) =
        let u = env.UserIdentity
        let r = env.DomainEventRepository
        let l = env.Logger
        let ct = CancellationToken.None
        {| GetByAggregateIdQuery = GetByAggregateIdQuery.runAsync u r l ct |}

open Fake
open Setup

// Per https://xunit.net/docs/running-tests-in-parallel, tests in a single
// class, called a test collection, are by default run in sequence. Tests across
// multiple classes are run in parallel, with the test inside individual classes
// running in sequence. To make a collection span multiple classes, they must
// share the same collection same. In addition, we can set other properties on
// the collection
[<CollectionDefinition(nameof DisableParallelization, DisableParallelization = true)>]
type DisableParallelization() =
    class
    end

// Serializing integration tests makes for slower but more reliable test runs.
// With SQLite, only one transaction can be in progress at once anyway. Another
// transaction will block on commit until the ongoing transaction finish commit
// or rollback. Thus commenting out the attribute below likely results in tests
// succeeding. But if any test assume a reset database, tests may start failing.
// In order for tests not to interfere with each other and the reset, take must
// be taken to serialize test runs.
[<Collection(nameof DisableParallelization)>]
type StoryAggregateRequestTests() =
    do reset ()

    [<Fact>]
    let ``must have member role to create story`` () =
        use env = appEnvWithRoles [ Admin ]
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.createStoryCommand ()
            let! result = fns.CreateStory storyCmd
            test <@ result = Error(CreateStoryCommand.AuthorizationError("Missing role 'member'")) @>
            do! fns.Commit()
        }                
    
    [<Fact>]
    let ``create story with task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let storyCmd = A.createStoryCommand ()
            let! result = fns.CreateStory storyCmd
            test <@ result = Ok(storyCmd.Id) @>
            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! result = fns.AddTaskToStory taskCmd
            test <@ result = Ok(taskCmd.TaskId) @>
            let! result = fns.GetStoryById { Id = taskCmd.StoryId }

            let story: StoryDto =
                { Id = storyCmd.Id
                  Title = storyCmd.Title
                  Description = storyCmd.Description |> Option.defaultValue null
                  CreatedAt = fixedClock.CurrentUtc()
                  UpdatedAt = None
                  Tasks =
                    [ { Id = taskCmd.TaskId
                        Title = taskCmd.Title
                        Description = taskCmd.Description |> Option.defaultValue null
                        CreatedAt = fixedClock.CurrentUtc()
                        UpdatedAt = None } ] }

            test <@ result = Ok(story) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``create duplicate story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let! result = fns.CreateStory cmd
            test <@ result = Error(CreateStoryCommand.DuplicateStory(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story without task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let! result = fns.DeleteStory { Id = cmd.Id }
            test <@ result = Ok(cmd.Id) @>
            let! result = fns.GetStoryById { Id = cmd.Id }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``delete story with task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let! result = fns.DeleteStory { Id = cmd.StoryId }
            test <@ result = Ok(cmd.StoryId) @>
            let! result = fns.GetStoryById { Id = cmd.StoryId }
            test <@ result = Error(GetStoryByIdQuery.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``add duplicate task to story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let createStoryCmd = A.createStoryCommand ()
            let addTaskCmd = { A.addTaskToStoryCommand () with StoryId = createStoryCmd.Id }
            let! _ = fns.CreateStory createStoryCmd
            let! _ = fns.AddTaskToStory addTaskCmd
            let! result = fns.AddTaskToStory addTaskCmd
            test <@ result = Error(AddTaskToStoryCommand.DuplicateTask(addTaskCmd.TaskId)) @>
        }

    [<Fact>]
    let ``add task to non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = { A.addTaskToStoryCommand () with StoryId = missingId () }
            let! result = fns.AddTaskToStory cmd
            test <@ result = Error(AddTaskToStoryCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { StoryId = cmd.StoryId; TaskId = cmd.TaskId }
            let! result = fns.DeleteTask cmd
            test <@ result = Ok(cmd.TaskId) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``delete task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let cmd = { StoryId = missingId (); TaskId = cmd.TaskId }
            let! result = fns.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

    [<Fact>]
    let ``delete non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { StoryId = cmd.Id; TaskId = missingId () }
            let! result = fns.DeleteTask cmd
            test <@ result = Error(DeleteTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = A.updateStoryCommand cmd
            let! result = fns.UpdateStory cmd
            test <@ result = Ok(cmd.Id) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``update non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let cmd = A.updateStoryCommand cmd
            let! result = fns.UpdateStory cmd
            test <@ result = Error(UpdateStoryCommand.StoryNotFound(cmd.Id)) @>
        }

    [<Fact>]
    let ``update task`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = A.updateTaskCommand cmd
            let! result = fns.UpdateTask cmd
            test <@ result = Ok(cmd.TaskId) @>
            do! fns.Commit()
        }

    [<Fact>]
    let ``update non-existing task on story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with TaskId = missingId () }
            let! result = fns.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.TaskNotFound(cmd.TaskId)) @>
        }

    [<Fact>]
    let ``update task on non-existing story`` () =
        use env = defaultAppEnv ()
        let fns = env |> setupStoryAggregateRequests
        task {
            let cmd = A.createStoryCommand ()
            let! _ = fns.CreateStory cmd
            let cmd = { A.addTaskToStoryCommand () with StoryId = cmd.Id }
            let! _ = fns.AddTaskToStory cmd
            let cmd = { A.updateTaskCommand cmd with StoryId = missingId () }
            let! result = fns.UpdateTask cmd
            test <@ result = Error(UpdateTaskCommand.StoryNotFound(cmd.StoryId)) @>
        }

[<Collection(nameof DisableParallelization)>]
type DomainEventRequestTests() =
    do reset ()

    [<Fact>]
    let ``query domain events`` () =
        use env = defaultAppEnv ()
        let sfns = env |> setupStoryAggregateRequests
        let dfns = env |> setupDomainEventRequests

        task {
            let storyCmd = A.createStoryCommand ()
            let! _ = sfns.CreateStory storyCmd
            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = sfns.AddTaskToStory taskCmd
            let! result = dfns.GetByAggregateIdQuery { Id = storyCmd.Id }

            // For this test, we can't use Unquote as we're not in control of Id
            // assigned to each domain event. We also forego testing the ordering
            // of events as they're created at the same time. Test could fail if
            // SQLite decides to change ordering of items with same CreatedAt.                        
            match result with
            | Ok r ->            
                Assert.Equal(2, r.Length)                
                Assert.Equal(storyCmd.Id, r[0].AggregateId)
                Assert.Equal("Story", r[0].AggregateType)
                Assert.Equal("TaskAddedToStory", r[0].EventType)
                Assert.Equal(fixedClock.CurrentUtc(), r[0].CreatedAt)
                
                Assert.Equal(storyCmd.Id, r[1].AggregateId)
                Assert.Equal("Story", r[1].AggregateType)
                Assert.Equal("StoryCreated", r[1].EventType)
                Assert.Equal(fixedClock.CurrentUtc(), r[1].CreatedAt)                               
            | Error e -> Assert.Fail($"%A{e}")   

            do! sfns.Commit()
        }

    [<Fact>]
    let ``must have admin role to query domain events`` () =
        use env = appEnvWithRoles [ Member ]
        let sfns = env |> setupStoryAggregateRequests
        let dfns = env |> setupDomainEventRequests
        
        task {
            let storyCmd = A.createStoryCommand ()
            let! _ = sfns.CreateStory storyCmd
            let taskCmd = { A.addTaskToStoryCommand () with StoryId = storyCmd.Id }
            let! _ = sfns.AddTaskToStory taskCmd
            let! result = dfns.GetByAggregateIdQuery { Id = storyCmd.Id }
            test <@ result = Error(GetByAggregateIdQuery.AuthorizationError("Missing role 'admin'")) @>
        }
        