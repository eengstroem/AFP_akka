// Learn more about F# at http://docs.microsoft.com/dotnet/fsharp
open System
open Akka.FSharp
open Akka.Actor
open System.IO

type Ticket = 
    {
        id:int
    }

type Event = 
    {
        Name:string
        AmountOfTickets: int
    }
    
type BoxofficeCommands = 
    | CreateEvent of Event
    | GetTickets of string
    | GetEvent of string
    | GetEvents
    | CancelEvent of string
    | BuyTicket of string * int
    | Tickets of Event
    
type TicketSellerCommands =
    | Add of Event
    | Buy of int
    | GetEvent
    | AvailableTickets  
    | Cancel 
    | PoisonPill
    
type DatabaseCommands =
    | CreateEvent of Event * (string -> unit) Option
    | CreateTickets of string * int
    | SellTickets of string * int
    | CancelEvent of string * (bool -> unit) Option
    | ShowStatus of string
    | GetEvent of string * (Ticket list -> unit) Option

type DatabaseResponse =
    | GetEventResponse of Event

type TicketSellerMessages = 
    | TicketSellerCommand of TicketSellerCommands
    | DatabaseCommand of DatabaseResponse

let ticketSellerActor (eventOuter: Event) (mailboxOuter: Actor<TicketSellerCommands>) = 
    let databaseActor = select "akka://TicketSystem/user/DatabaseSupervisor/Database" mailboxOuter.Context.System
    
    let rec mainActor (event: Event) (mailbox: Actor<TicketSellerCommands>) = actor{
        let! msg = mailbox.Receive()
        
        match msg with
        | TicketSellerCommands.Add({Name = name; AmountOfTickets = amountOfTickets}) ->  
            let newEvent = {Name = name; AmountOfTickets =  amountOfTickets}
            databaseActor <! DatabaseCommands.CreateEvent (newEvent, None)
            databaseActor <! DatabaseCommands.CreateTickets (newEvent.Name, newEvent.AmountOfTickets)
            return! created newEvent.Name mailbox
        | _ -> mailbox.Sender () <! "Error: Unknown command"
    }
    and created(eventName: string) (mailbox: Actor<TicketSellerCommands>) = actor{ //State
        let! msg = mailbox.Receive()
        
        match msg with
        | AvailableTickets ->
            databaseActor <! DatabaseCommands.ShowStatus
            return! created eventName mailbox
            
        | TicketSellerCommands.GetEvent ->
            let databaseActorr = select "akka://TicketSystem/user/DatabaseSupervisor/Database" mailboxOuter.Context.System
            databaseActorr <! DatabaseCommands.GetEvent (eventName, Some (fun tickets -> printfn "Tickets for event %A: %A" eventName tickets))
            return! created eventName mailbox
            
        | Cancel -> return! cancelled eventName mailbox
        
        | Buy (noOfTickets) ->
            databaseActor <! DatabaseCommands.SellTickets (eventName, noOfTickets)
            return! created eventName mailbox
                                    
    }
    and cancelled(eventName: string) (mailbox: Actor<TicketSellerCommands>) = actor{ //State
        databaseActor <! DatabaseCommands.CancelEvent (eventName, Some (fun success -> if success then printfn "Cancelled event %A" eventName
                                                                                       else printfn "Could not cancel event %A" eventName))
        printfn "Event name: %A cancelled \n" eventName
        mailbox.Sender () <! (eventName)
        mailbox.Self <! PoisonPill
    }
        
    mainActor eventOuter mailboxOuter
    


let boxOfficeActor (mailbox: Actor<BoxofficeCommands>) command =
    match command with
        | BoxofficeCommands.CreateEvent {Name = name; AmountOfTickets = amountOfTickets} when amountOfTickets > 0 -> 
            let ticketSeller = spawn mailbox.Context name (ticketSellerActor {Name = name; AmountOfTickets = amountOfTickets; })
            ticketSeller <! (Add {Name = name; AmountOfTickets = amountOfTickets; })
            
        | BoxofficeCommands.GetTickets name -> 
            let ticketseller = mailbox.Context.Child name
            ticketseller.Forward(AvailableTickets)
        
        | BoxofficeCommands.GetEvent name -> 
            let ticketseller = mailbox.Context.Child name
            ticketseller.Forward(TicketSellerCommands.GetEvent)
            
        | BoxofficeCommands.GetEvents -> 
            let ticketseller = mailbox.Context.GetChildren()
            for seller in ticketseller do
                seller.Forward(TicketSellerCommands.GetEvent)
                
        | BoxofficeCommands.CancelEvent name -> 
            let ticketseller = mailbox.Context.Child name
            ticketseller <! (Cancel)
            
        | BoxofficeCommands.BuyTicket (name, noOfTickets) -> 
            let ticketseller = mailbox.Context.Child name
            ticketseller <! (Buy noOfTickets)

        | _ -> printfn " - Unknown command"
        

let convertMessageToBoxOfficeCommand (msg : string) : Option<BoxofficeCommands> = 
    let splitted = msg.Split(' ')
        
    match splitted[0].ToLower() with
    | "createevent" -> Some (BoxofficeCommands.CreateEvent {Name = splitted[1]; AmountOfTickets= ((int)splitted[2]); })
    | "gettickets" -> Some (BoxofficeCommands.GetTickets splitted[1])
    | "getevent" -> Some (BoxofficeCommands.GetEvent splitted[1])
    | "getevents" -> Some BoxofficeCommands.GetEvents
    | "cancelevent" -> Some (BoxofficeCommands.CancelEvent splitted[1])
    | "buyticket" -> Some (BoxofficeCommands.BuyTicket (splitted[1], ((int)splitted[2])))
    | _ -> None
    

// Input actor taking in a boxoffice and ignores all messages
let inputActor boxOffice (mailbox: Actor<_>) _ =
    printf "\nEnter input: "
    let txt = Console.ReadLine()
    match (txt.ToLower()) with
    | "exit" -> mailbox.Context.System.Terminate() |> ignore
    | _ ->
        match (convertMessageToBoxOfficeCommand txt) with
        | Some cmd -> boxOffice <! cmd
        | None -> printfn "Not a valid command. Try again."
        mailbox.Self <! ""


// Without full Dictionary namespace the for-loop in TicketSellerActor does not compile..... Full namespace is the solution to avoid debugging time.
let database = new System.Collections.Generic.Dictionary<string, Ticket list>();

let databaseActor (mailbox: Actor<DatabaseCommands>) =
    let rec loop () = actor{
        let! msg = mailbox.Receive()

        // Random fail
        let rnd = System.Random()
        let randomValue = rnd.Next(0, 7);
        if randomValue = 5 then
            raise (new IOException("Oh no! An database error occurred. Datacenter is on fire!!!!"))
        
        else 
        match msg with
            | DatabaseCommands.CreateEvent (event, fn) -> 
                database.Add(event.Name, list.Empty)
                Option.map (fun u -> u event.Name) fn |> ignore
            | DatabaseCommands.CreateTickets (eventName, numberOfTickets) ->
                database[eventName] <- [ for i in 1 .. numberOfTickets -> {id=i} ]
                printfn "Tickets created: %A" database[eventName]
            | DatabaseCommands.SellTickets (eventName, numberOfTickets) ->
                let tickets = database[eventName]
                match (List.length tickets >= numberOfTickets) with
                                    | true -> 
                                        let newTickets = List.removeManyAt 0 numberOfTickets tickets
                                        database[eventName] <- newTickets
                                        printfn "Tickets sold.\nTickets left: %A" newTickets
                                        mailbox.Sender () <! "Successfull purchase"
                                    | false -> 
                                        printfn "Not enough tickets available"
                                        mailbox.Sender () <! "Error: Not enough tickets"
            | DatabaseCommands.CancelEvent (eventName, fn) ->
                // True if event was found and removed
                let success = database.Remove(eventName) 
                Option.map (fun u -> u success) fn |> ignore // Pass success variable to u which is the function sent from the caller
            | DatabaseCommands.ShowStatus eventName ->
                let ticketsForEvent = database[eventName]
                printfn "Tickets for event %A: %A" eventName ticketsForEvent
            | DatabaseCommands.GetEvent (eventName, fn) -> 
                let ticketsForEvent = database[eventName]
                Option.map (fun u -> u ticketsForEvent) fn |> ignore
                mailbox.Sender () <! database[eventName]
            | _ -> printfn " - Command not implemented" // This will never be hit, but in the future if anyone should add more commands, this will prevent mistakes
        return! loop ()
    }
    loop()

let databaseSupervisorActor (mailbox: Actor<_>) =
    actor {
        spawn mailbox.Context "Database" databaseActor |> ignore
    }



[<EntryPoint>]
let main argv =

    let actorSystem = System.create "TicketSystem" (Configuration.load())
    let spawnedBoxOfficeActor = spawn actorSystem "BoxOfficeActor" (actorOf2 boxOfficeActor)
    let spawnedInputActor = spawn actorSystem "InputActor" (actorOf2 (inputActor spawnedBoxOfficeActor))
    let spawnedDatabaseSupervisor = spawnOpt actorSystem "DatabaseSupervisor" databaseSupervisorActor
                                         [ SpawnOption.SupervisorStrategy (Strategy.OneForOne (fun error ->
                                                match error with
                                                    // maybe non-critical, ignore & keep going
                                                | :? ArithmeticException -> Directive.Resume
                                                    // Lad actoren køre som om intet er sket
                                                | :? NotSupportedException -> 
                                                    printfn "Database error caught: %A" error
                                                    Directive.Stop
                                                    // Stop actoren
                                                | :? IOException ->
                                                    printfn "Database IO error caught: %A" error
                                                    Directive.Restart
                                                | _ -> 
                                                    printfn "Restarting database due to unknown error. \n. Error message: %A" error.Message
                                                    Directive.Restart // Prøv at genstarte actoren
                                         ))]
    
    printfn "Box office started"
    spawnedInputActor <! ""
    actorSystem.WhenTerminated.Wait ()
    0