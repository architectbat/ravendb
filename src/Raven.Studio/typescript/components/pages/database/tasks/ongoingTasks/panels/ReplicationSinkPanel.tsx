﻿import React from "react";
import {
    BaseOngoingTaskPanelProps,
    ConnectionStringItem,
    OngoingTaskActions,
    OngoingTaskName,
    OngoingTaskResponsibleNode,
    OngoingTaskStatus,
    useTasksOperations,
} from "../../shared/shared";
import { OngoingTaskReplicationSinkInfo } from "components/models/tasks";
import { useAccessManager } from "hooks/useAccessManager";
import { useAppUrls } from "hooks/useAppUrls";
import {
    RichPanel,
    RichPanelActions,
    RichPanelDetailItem,
    RichPanelDetails,
    RichPanelHeader,
    RichPanelInfo,
    RichPanelSelect,
} from "components/common/RichPanel";
import { Collapse, Input } from "reactstrap";

type ReplicationSinkPanelProps = BaseOngoingTaskPanelProps<OngoingTaskReplicationSinkInfo>;

function Details(props: ReplicationSinkPanelProps & { canEdit: boolean }) {
    const { data, canEdit, db } = props;
    const connectionStringDefined = !!data.shared.destinationDatabase;
    const { appUrl } = useAppUrls();
    const connectionStringsUrl = appUrl.forConnectionStrings(db, "Raven", data.shared.connectionStringName);

    return (
        <RichPanelDetails>
            <RichPanelDetailItem label="Hub Name">{data.shared.hubName}</RichPanelDetailItem>
            <ConnectionStringItem
                connectionStringDefined={!!data.shared.destinationDatabase}
                canEdit={canEdit}
                connectionStringName={data.shared.connectionStringName}
                connectionStringsUrl={connectionStringsUrl}
            />
            {connectionStringDefined && (
                <RichPanelDetailItem label="Hub Database">{data.shared.destinationDatabase}</RichPanelDetailItem>
            )}
            <RichPanelDetailItem label="Actual Hub URL">{data.shared.destinationUrl ?? "N/A"}</RichPanelDetailItem>

            {data.shared.topologyDiscoveryUrls.map((url) => (
                <RichPanelDetailItem label="Topology Discovery URL" key={url}>
                    {url}
                </RichPanelDetailItem>
            ))}
        </RichPanelDetails>
    );
}

export function ReplicationSinkPanel(props: ReplicationSinkPanelProps) {
    const { db, data, toggleSelection, isSelected, onTaskOperation, isDeleting, isTogglingState } = props;

    const { isAdminAccessOrAbove } = useAccessManager();
    const { forCurrentDatabase } = useAppUrls();

    const canEdit = isAdminAccessOrAbove(db) && !data.shared.serverWide;
    const editUrl = forCurrentDatabase.editReplicationSink(data.shared.taskId)();

    const { detailsVisible, toggleDetails, onEdit } = useTasksOperations(editUrl, props);

    return (
        <RichPanel>
            <RichPanelHeader>
                <RichPanelInfo>
                    {canEdit && (
                        <RichPanelSelect>
                            <Input
                                type="checkbox"
                                onChange={(e) => toggleSelection(e.currentTarget.checked, data.shared)}
                                checked={isSelected(data.shared.taskId)}
                            />
                        </RichPanelSelect>
                    )}
                    <OngoingTaskName task={data} canEdit={canEdit} editUrl={editUrl} />
                </RichPanelInfo>
                <RichPanelActions>
                    <OngoingTaskResponsibleNode task={data} />
                    <OngoingTaskStatus
                        task={data}
                        canEdit={canEdit}
                        onTaskOperation={onTaskOperation}
                        isTogglingState={isTogglingState(data.shared.taskId)}
                    />
                    <OngoingTaskActions
                        task={data}
                        canEdit={canEdit}
                        onEdit={onEdit}
                        onTaskOperation={onTaskOperation}
                        toggleDetails={toggleDetails}
                        isDeleting={isDeleting(data.shared.taskId)}
                    />
                </RichPanelActions>
            </RichPanelHeader>
            <Collapse isOpen={detailsVisible}>
                <Details {...props} canEdit={canEdit} />
            </Collapse>
        </RichPanel>
    );
}
