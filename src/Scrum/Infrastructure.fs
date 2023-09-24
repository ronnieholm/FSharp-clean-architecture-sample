﻿namespace Scrum.Infrastructure

open System
open System.Threading
open System.Threading.Tasks
open System.Data.SQLite
open FsToolkit.ErrorHandling
open Scrum.Application.Seedwork
open Scrum.Domain.StoryAggregate
open Scrum.Domain.StoryAggregate.TaskEntity

type SqliteStoryRepository(transaction: SQLiteTransaction) =
    let connection = transaction.Connection

    interface IStoryRepository with
        member _.ExistAsync (ct: CancellationToken) (id: StoryId) : Task<bool> =
            let sql = "select count(*) from stories where id = @id"
            use cmd = new SQLiteCommand(sql, connection, transaction)
            cmd.Parameters.AddWithValue("@id", id |> StoryId.value |> string) |> ignore
            task {
                let! count = cmd.ExecuteScalarAsync(ct)
                let exist =
                    match count :?> int64 with
                    | 0L -> false
                    | 1L -> true
                    | _ -> failwith $"Inconsistent database state. Duplicate story Id: '{id}'"
                return exist
            }

        member _.GetByIdAsync (ct: CancellationToken) (id: StoryId) : Task<Story option> =
            // TODO: Convert to single SQL with left inner join.
            let deserializeTasks (r: SQLiteDataReader) =
                let rec aux tasks =
                    if r.Read() then
                        let taskId = r["id"] |> string |> Guid |> TaskId
                        let title = r["title"] |> string |> TaskTitle
                        let description =
                            if r["description"] = DBNull.Value then
                                None
                            else
                                r["description"] |> string |> TaskDescription |> Some
                        let createdAt = r["created_at"] |> string |> DateTime.Parse
                        let updatedAt =
                            if r["updated_at"] = DBNull.Value then
                                None
                            else
                                r["updated_at"] |> string |> DateTime.Parse |> Some

                        let task =
                            { Entity = { Id = taskId; CreatedAt = createdAt; UpdatedAt = updatedAt }
                              Title = title
                              Description = description }

                        aux (task :: tasks)
                    else
                        tasks

                aux []

            // We could've let deserializeStories call into deserializeTasks,
            // but then deserializeStories cannot be reused elsewhere in a
            // context where we're aren't interested in tasks. At least not
            // without adding a switch to not progress beyond story.
            let deserializeStories (r: SQLiteDataReader) =
                let rec aux stories =
                    if r.Read() then
                        let storyId = r["id"] |> string |> Guid |> StoryId
                        let title = r["title"] |> string |> StoryTitle
                        let description =
                            if r["description"] = DBNull.Value then
                                None
                            else
                                r["description"] |> string |> StoryDescription |> Some
                        let createdAt = string r["created_at"] |> DateTime.Parse
                        let updatedAt =
                            if r["updated_at"] = DBNull.Value then
                                None
                            else
                                r["updated_at"] |> string |> DateTime.Parse |> Some

                        let story: Story =
                            { Root = { Id = storyId; CreatedAt = createdAt; UpdatedAt = updatedAt }
                              Title = title
                              Description = description
                              Tasks = [] }

                        aux (story :: stories)
                    else
                        stories

                aux []

            let storySql = "select * from stories where id = @id"
            use getStories = new SQLiteCommand(storySql, connection, transaction)
            getStories.Parameters.AddWithValue("@id", id |> StoryId.value |> string)
            |> ignore

            let taskSql = "select * from tasks where story_id = @storyId"
            use getTasks = new SQLiteCommand(taskSql, connection, transaction)
            getTasks.Parameters.AddWithValue("@storyId", id |> StoryId.value |> string)
            |> ignore

            let tasks = deserializeTasks (getTasks.ExecuteReader())

            deserializeStories (getStories.ExecuteReader())
            |> List.exactlyOne
            |> fun s -> { s with Tasks = tasks }
            |> Some
            |> Task.FromResult

        // As we're immediately applying events to the store, we don't have to
        // worry about events evolving over time. For this domain, we don't
        // require full event sourcing; only enough event data to keep the store up to
        // date.
        member _.ApplyEventAsync (ct: CancellationToken) (event: DomainEvent) : Task<unit> =
            // TODO: persist event to DomainEvents table
            match event with
            | DomainEvent.StoryCreatedEvent e ->
                let description =
                    match e.StoryDescription with
                    | Some x -> x |> StoryDescription.value
                    | None -> null

                let sql =
                    "insert into stories (id, title, description, created_at) values (@id, @title, @description, @createdAt)"
                use cmd = new SQLiteCommand(sql, connection, transaction)
                let p = cmd.Parameters
                p.AddWithValue("@id", e.StoryId |> StoryId.value |> string) |> ignore
                p.AddWithValue("@title", e.StoryTitle |> StoryTitle.value) |> ignore
                p.AddWithValue("@description", description) |> ignore
                p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore

                task {
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                    return ()
                }
            | DomainEvent.StoryUpdatedEvent e -> failwith "Not implemented"
            | DomainEvent.StoryDeletedEvent e -> failwith "Not implemented"
            | DomainEvent.TaskAddedToStoryEvent e ->
                let description =
                    match e.TaskDescription with
                    | Some x -> x |> TaskDescription.value
                    | None -> null

                let sql =
                    "insert into tasks (id, story_id, title, description, created_at) values (@id, @storyId, @title, @description, @createdAt)"
                use cmd = new SQLiteCommand(sql, connection, transaction)
                let p = cmd.Parameters
                p.AddWithValue("@id", e.TaskId |> TaskId.value |> string) |> ignore
                p.AddWithValue("@storyId", e.StoryId |> StoryId.value |> string) |> ignore
                p.AddWithValue("@title", e.TaskTitle |> TaskTitle.value) |> ignore
                p.AddWithValue("@description", description) |> ignore
                p.AddWithValue("@createdAt", string e.CreatedAt) |> ignore

                task {
                    let! count = cmd.ExecuteNonQueryAsync(ct)
                    assert (count = 1)
                    return ()
                }
            | DomainEvent.TaskUpdatedEvent e -> failwith "Not implemented"
            | DomainEvent.TaskDeletedEvent e -> failwith "Not implemented"

type SystemClock() =
    interface ISystemClock with
        member this.CurrentUtc() = DateTime.UtcNow

type Logger() =
    interface ILogger with
        member _.LogRequestPayload (useCase: string) (request: obj) : unit = printfn $"%s{useCase}: %A{request}"
        member _.LogRequestTime (useCase: string) (elapsed: uint<ms>) : unit = printfn $"%s{useCase}: %d{elapsed}"

// Pass into ctor IOptions<config>.
// Avoid Singletons dependencies as they make testing hard.
// transient dependencies are implemented like scoped factory method, except without lazy instantiation.
//member _.SomeTransientDependency = newedUpDependency
// In principle, currentTime could be a singleton, except it doesn't work well
// when we want to switch out the time provider in tests. If we kept currentTime
// a singleton changing the provider in one test would affect the others.
// Are Open and BeginTransaction on the connection idempotent?
// AppEnv is an example of the service locator pattern in use. Ideal when passing AppEnv to Notification which passes it along. Hard to accomplish with partial application. Although in OO the locater tends to be a static class. DI degenerates to service locator when classes not instantiated by framework code.
// This is our composition root: https://blog.ploeh.dk/2011/07/28/CompositionRoot/
type AppEnv(connectionString: string, ?systemClock, ?logger, ?storyRepository) =
    let transaction =
        lazy
            let connection = new SQLiteConnection(connectionString)
            connection.Open()
            connection.BeginTransaction()

    let storyRepository' =
        lazy (storyRepository |> Option.defaultValue (SqliteStoryRepository transaction.Value))

    let systemClock' = lazy (systemClock |> Option.defaultValue (SystemClock()))
    let logger' = lazy (logger |> Option.defaultValue (Logger()))

    interface IAppEnv with
        member _.CommitAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            if transaction.IsValueCreated then
                transaction.Value.CommitAsync(ct) // TODO: Must be awaited
            else
                Task.CompletedTask

        member _.RollbackAsync(ct: CancellationToken) : System.Threading.Tasks.Task =
            // TODO: Should we have a dispose() which calls Rollback? How to not get stuck?
            if transaction.IsValueCreated then
                transaction.Value.RollbackAsync(ct) // TODO: Must be awaited
            else
                Task.CompletedTask

        member _.SystemClock = systemClock'.Value
        member _.Logger = logger'.Value
        member _.StoryRepository = storyRepository'.Value
