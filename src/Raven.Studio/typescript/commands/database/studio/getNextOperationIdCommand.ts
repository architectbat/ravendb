import commandBase = require("commands/commandBase");
import database = require("models/resources/database");
import endpoints = require("endpoints");

class getNextOperationIdCommand extends commandBase {

    private readonly db: database;

    constructor(db: database) {
        super();
        this.db = db;
    }

    execute(): JQueryPromise<number> {
        const url = this.db ? endpoints.databases.operations.operationsNextOperationId : endpoints.global.operationsServer.adminOperationsNextOperationId;
        return this.query(url, null, this.db, x => x.Id);
    }
}

export = getNextOperationIdCommand; 
