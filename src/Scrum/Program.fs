﻿namespace Scrum.Web

open System
open System.Diagnostics
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Hosting
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.DependencyInjection
open Microsoft.AspNetCore.Mvc
open Microsoft.Extensions.Primitives
open Scrum.Application.Seedwork
open Scrum.Application.StoryAggregateRequest
open Scrum.Infrastructure
open System.Text.Json

// RFC7807 error format
type ErrorDto = { Type: string; Title: string; Status: int; Detail: string }
module ErrorDto =
    let create status detail : ErrorDto = { Type = "Error"; Title = "Error"; Status = status; Detail = detail }

    let toJsonResult (accept: StringValues) (error: ErrorDto) : ActionResult =
        let r = JsonResult(error)
        r.StatusCode <- error.Status

        // Support problem JSON as per https://opensource.zalando.com/restful-api-guidelines/#176.
        let h = accept.ToArray() |> Array.exists (fun v -> v = "application/problem+json")
        r.ContentType <- if h then "application/problem+json" else "application/json"
        r :> ActionResult

    let createJsonResult (accept: StringValues) status detail : ActionResult = create status detail |> toJsonResult accept

    type ValidationErrorDto = { Field: string; Message: string }
    
    let fromValidationErrors (accept: StringValues) (errors: ValidationError list) : ActionResult =
        let errors =
            errors
            |> List.map (fun e -> { Field = e.Field; Message = e.Message })
            |> JsonSerializer.Serialize // TODO: Use same options and formatters and ASP.NET pipeline.
        createJsonResult accept StatusCodes.Status400BadRequest errors

    let fromException (accept: StringValues) : ActionResult =
        createJsonResult accept StatusCodes.Status500InternalServerError "Internal server error"

[<ApiController>]
[<Route("[controller]")>]
type ScrumController() =
    inherit ControllerBase()

    let env = new AppEnv("URI=file:/home/rh/Downloads/scrumfs.sqlite") :> IAppEnv

    member _.Env = env

    [<NonAction>]
    member x.HandleExceptionAsync (e: exn) (acceptHeaders: StringValues) (ct: CancellationToken) : Task<ActionResult> =
        task {
            x.Env.Logger.LogException(e)
            do! x.Env.RollbackAsync(ct)
            return ErrorDto.fromException acceptHeaders
        }

    interface IDisposable with
        member this.Dispose() = this.Env.Dispose()

type StoryCreateDto = { title: string; description: string }
type StoryUpdateDto = { title: string; description: string }
type AddTaskToStoryDto = { title: string; description: string }
type StoryTaskUpdateDto = { title: string; description: string }

[<ApiController>]
[<Route("[controller]")>]
type StoriesController() =
    inherit ScrumController()

    // Success: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'
    // Failure: curl https://localhost:5000/stories --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": ""}' | jq

    [<HttpPost>]
    member x.CreateStory([<FromBody>] request: StoryCreateDto, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result =
                    CreateStoryCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { Id = Guid.NewGuid()
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                    | Error e ->
                        match e with
                        | CreateStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | CreateStoryCommand.DuplicateStory id -> raise (UnreachableException(string id))
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e --insecure --request put -H 'Content-Type: application/json' -d '{"title": "title1","description": "description1"}'

    [<HttpPut>]
    [<Route("{id}")>]
    member x.UpdateStory([<FromBody>] request: StoryUpdateDto, id: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result =
                    UpdateStoryCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { Id = id
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok id -> CreatedResult($"/stories/{id}", id) :> ActionResult
                    | Error e ->
                        match e with
                        | UpdateStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | UpdateStoryCommand.StoryNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // curl https://localhost:5000/stories/fec32101-72b0-4d96-814f-de1c5b2dd140 --insecure --request delete

    [<HttpDelete>]
    [<Route("{id}")>]
    member x.DeleteStory(id: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result = DeleteStoryCommand.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok _ -> OkResult() :> ActionResult
                    | Error e ->
                        match e with
                        | DeleteStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | DeleteStoryCommand.StoryNotFound _ ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks/57db7489-722f-4d66-97d5-d5c2501eb89e --insecure --request delete

    [<HttpDelete>]
    [<Route("{storyId}/tasks/{taskId}")>]
    member x.DeleteTaskFromStory(storyId: Guid, taskId: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result = DeleteTaskCommand.runAsync x.Env.StoryRepository x.Env.Logger ct { StoryId = storyId; TaskId = taskId }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok _ -> OkResult() :> ActionResult
                    | Error e ->
                        match e with
                        | DeleteTaskCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | DeleteTaskCommand.StoryNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                        | DeleteTaskCommand.TaskNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // Success: curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks --insecure --request post -H 'Content-Type: application/json' -d '{"title": "title","description": "description"}'

    [<HttpPost>]
    [<Route("{storyId}/tasks")>]
    member x.AddTaskToStory([<FromBody>] request: AddTaskToStoryDto, storyId: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result =
                    AddTaskToStoryCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { TaskId = Guid.NewGuid()
                          StoryId = storyId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok taskId -> CreatedResult($"/stories/{storyId}/tasks/{taskId}", taskId) :> ActionResult
                    | Error e ->
                        match e with
                        | AddTaskToStoryCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | AddTaskToStoryCommand.StoryNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                        | AddTaskToStoryCommand.DuplicateTask id -> raise (UnreachableException(string id))
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e/tasks/916397d3-0c10-495c-a6e3-a081d41f644c --insecure --request put -H 'Content-Type: application/json' -d '{"title": "title1","description": "description1"}'

    [<HttpPut>]
    [<Route("{storyId}/tasks/{taskId}")>]
    member x.UpdateTaskOnStory
        (
            [<FromBody>] request: StoryTaskUpdateDto,
            storyId: Guid,
            taskId: Guid,
            ct: CancellationToken
        ) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result =
                    UpdateTaskCommand.runAsync
                        x.Env.StoryRepository
                        x.Env.SystemClock
                        x.Env.Logger
                        ct
                        { StoryId = storyId
                          TaskId = taskId
                          Title = request.title
                          Description = request.description |> Option.ofObj }
                do! x.Env.CommitAsync(ct)
                return
                    match result with
                    | Ok _ -> OkResult() :> ActionResult
                    | Error e ->
                        match e with
                        | UpdateTaskCommand.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | UpdateTaskCommand.StoryNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
                        | UpdateTaskCommand.TaskNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Task not found: '{string id}'"
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

    // curl https://localhost:5000/stories/bad0f0bd-6a6a-4251-af62-477513fad87e --insecure --request get | jq

    [<HttpGet>]
    [<Route("{id}")>]
    member x.GetByStoryId(id: Guid, ct: CancellationToken) : Task<ActionResult> =
        task {
            let accept = x.Request.Headers.Accept
            try
                let! result = GetStoryByIdQuery.runAsync x.Env.StoryRepository x.Env.Logger ct { Id = id }
                return
                    match result with
                    | Ok s -> OkObjectResult(s) :> ActionResult
                    | Error e ->
                        match e with
                        | GetStoryByIdQuery.ValidationErrors ve -> ErrorDto.fromValidationErrors accept ve
                        | GetStoryByIdQuery.StoryNotFound id ->
                            ErrorDto.createJsonResult accept StatusCodes.Status404NotFound $"Story not found: '{string id}'"
            with e ->
                return! x.HandleExceptionAsync e accept ct
        }

module JsonSerialization =

    ()

type Startup() =
    member _.ConfigureServices(services: IServiceCollection) : unit =
        services.AddMvc(fun options -> options.EnableEndpointRouting <- false) |> ignore
        services.AddControllers() |> ignore
        services.AddResponseCaching() |> ignore
        services.AddEndpointsApiExplorer() |> ignore

    member _.Configure (app: IApplicationBuilder) (env: IWebHostEnvironment) : unit =
        if env.IsDevelopment() then app.UseDeveloperExceptionPage() |> ignore else ()

        app.UseHttpsRedirection() |> ignore
        app.UseResponseCaching() |> ignore
        app.UseRouting() |> ignore
        app.UseMvcWithDefaultRoute() |> ignore

module Program =
    let createHostBuilder args : IHostBuilder =
        Host
            .CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(fun builder -> builder.UseStartup<Startup>() |> ignore)

    [<EntryPoint>]
    let main args =
        // Short-hand initialization. .NET 7 moved away from Configure and ConfigureServices, but still support those.
        // - https://learn.microsoft.com/en-us/aspnet/core/migration/50-to-60-samples?view=aspnetcore-7.0
        // - https://mobiletonster.com/blog/code/aspnet-core-6-how-to-deal-with-the-missing-startupcs-file
        //
        // let builder = WebApplication.CreateBuilder(args)
        // builder.Services.AddControllers() |> ignore
        // let app = builder.Build()
        // app.UseHttpsRedirection() |> ignore
        // app.MapControllers() |> ignore
        // app.Run()

        let host = createHostBuilder(args).Build()
        host.Run()
        0
